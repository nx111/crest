#!/usr/bin/env python3
"""Check actual wave-normal expressions against independent surface geometry.

CPU numerical/source checks with IEEE binary16/binary32 sample round trips.
This does not execute Unity, GPU texture filtering or a complete LOD mesh.
Use --ref <commit> to evaluate an earlier implementation without editing files.
"""
import argparse
import math
import re
import struct
import sys
import unittest

import check_urp_regressions as source
from check_optical_quality import Vector, dot, length, evaluate

HELPERS = source.CREST + "Shaders/OceanHelpersNew.hlsl"


def cross(a, b):
    return Vector((a.y * b.z - a.z * b.y,
                   a.z * b.x - a.x * b.z,
                   a.x * b.y - a.y * b.x))


def stored(value, precision):
    if isinstance(value, tuple):
        return Vector(stored(item, precision) for item in value)
    code = "e" if precision.startswith("half") else "f"
    return struct.unpack("<" + code, struct.pack("<" + code, value))[0]


def assign(values, name, value):
    if "." not in name:
        values[name] = value
        return
    variable, components = name.split(".")
    vector = list(values[variable])
    for component, item in zip(components, value if isinstance(value, tuple) else (value,)):
        vector["xyzw".index(component)] = item
    values[variable] = Vector(vector)


class WaveNormalModel:
    def __init__(self, text):
        self.text = text
        self.body = source.body(text, "SampleDisplacementsNormals")
        self.functions = {"cross": cross, "float2": lambda *items: Vector(items)}
        if "float2 CrestWaveNormalSlope(" in text:
            self.functions["CrestWaveNormalSlope"] = lambda x, z: self.run(
                source.body(text, "CrestWaveNormalSlope"), {"tangentX": x, "tangentZ": z})

    def run(self, block, values):
        # SSS has its own determinant accumulator and does not affect the normal.
        block = re.sub(r"#if _SUBSURFACESCATTERING_ON.*?#endif", "", block, flags=re.S)
        block = block.replace("{", "").replace("}", "")
        for statement in block.split(";"):
            statement = statement.strip()
            if not statement:
                continue
            if statement.startswith("return "):
                return evaluate(statement[7:], values, self.functions)
            declaration = re.match(r"(?:const\s+)?(float[234]?|half[234]?)\s+", statement)
            if declaration:
                statement = statement[declaration.end():]
            if re.fullmatch(r"\w+", statement) and declaration:
                continue
            match = re.fullmatch(r"(\w+(?:\.[xyzw]+)?)\s*([+*]?=)\s*(.+)", statement, re.S)
            if not match:
                raise AssertionError(f"Unsupported wave-normal statement: {statement}")
            name, operation, expression = match.groups()
            if "i_dispSampler.SampleLevel(" in expression:
                sample = {"data": "sample0", "disp_x": "sampleX", "disp_z": "sampleZ"}[name]
                expression = re.sub(r"i_dispSampler\.SampleLevel\(.+\)", sample, expression)
            # Preserve the actual swizzle while keeping the shared evaluator's
            # intentionally small allowlist unchanged.
            for swizzle in ("zyy", "yyz"):
                if "dd." + swizzle in expression:
                    variable = "dd_" + swizzle
                    values[variable] = getattr(values["dd"], swizzle)
                    expression = expression.replace("dd." + swizzle, variable)
            value = evaluate(expression, values, self.functions)
            if operation != "=":
                previous = evaluate(name, values, self.functions)
                value = previous + value if operation == "+=" else previous * value
            # Explicit sample/storage casts are the precision risk under test.
            # Other formula arithmetic uses CPU double precision, not a GPU model.
            if declaration and name in ("data", "disp", "disp_x", "disp_z"):
                value = stored(value, declaration[1])
            assign(values, name, value)
        return values

    def slopes(self, tangent_x, tangent_z, cell=1.0, centre=(0, 0, 0), weight=1.0):
        centre = Vector(centre)
        # Texture samples contain displacement only; the shader adds the flat
        # X/Z lattice step before constructing each surface tangent.
        dx = (Vector(tangent_x) - Vector((1, 0, 0))) * cell
        dz = (Vector(tangent_z) - Vector((0, 0, 1))) * cell
        values = {
            "sample0": stored(Vector((*centre, 0)), "float4"),
            "sampleX": stored(Vector((*(centre + dx), 0)), "float4"),
            "sampleZ": stored(Vector((*(centre + dz), 0)), "float4"),
            "i_invRes": 1 / 256, "i_texelSize": cell, "i_wt": weight,
            "io_worldPos": Vector((0, 0, 0)), "io_nxz": Vector((0, 0)),
        }
        return self.run(self.body, values)["io_nxz"]


class WaveNormals(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.model = WaveNormalModel(source.source(HELPERS))
        ocean = source.source(source.OCEAN)
        cls.initial = re.search(r"float3 n_pixel\s*=\s*([^;]+);", ocean)[1]
        cls.strength = re.search(r"n_pixel.xz\s*\*=\s*([^;]+);", ocean)[1]
        cls.normalise = re.search(r"n_pixel\s*=\s*(normalize\s*\([^;]+\));", ocean)[1]

    def finish(self, slopes, strength=1.0):
        normal = evaluate(self.initial, {})
        values = {"n_pixel": normal, "_NormalsStrengthOverall": strength}
        amount = evaluate(self.strength, values)
        values["n_pixel"] = Vector((normal.x + slopes.x * amount, normal.y, normal.z + slopes.y * amount))
        return evaluate(self.normalise, values)

    def assertVector(self, actual, expected, tolerance=2e-6):
        for a, e in zip(actual, expected):
            self.assertAlmostEqual(a, e, delta=tolerance)

    def test_wave_normals_are_perpendicular_to_affine_surface_tangents(self):
        # Independent geometric invariant: a surface normal is perpendicular to
        # both tangent directions, regardless of the normal accumulation scheme.
        for x, z in (((1, 0, 0), (0, 0, 1)), ((1, .5, 0), (0, -.25, 1)),
                     ((.25, 1.5, .125), (.125, -.75, 2)),
                     ((1.5, -2, .5), (-.25, 1.25, .75))):
            actual = self.finish(self.model.slopes(x, z))
            self.assertAlmostEqual(length(actual), 1.0, places=12)
            self.assertAlmostEqual(dot(actual, x) / length(x), 0.0, delta=2e-6)
            self.assertAlmostEqual(dot(actual, z) / length(z), 0.0, delta=2e-6)
            self.assertGreater(actual.y, 0)

    def test_steep_wave_tilt_is_not_capped_by_unit_normal_xz(self):
        for degrees in (0, 5, 20, 45, 60, 75, 85):
            slope = math.tan(math.radians(degrees))
            for azimuth in (0, .4, 1.8, 3.7):
                a, b = slope * math.cos(azimuth), slope * math.sin(azimuth)
                actual = self.finish(self.model.slopes((1, a, 0), (0, b, 1)))
                self.assertAlmostEqual(math.degrees(math.acos(actual.y)), degrees, delta=1e-5)

    def test_same_surface_has_same_normal_at_different_lod_texel_sizes(self):
        x, z = (1, 2, 0), (0, -.5, 1)
        reference = self.finish(self.model.slopes(x, z, cell=1))
        for cell in (.0001, .001, .015625, .25, 1, 8, 128):
            self.assertVector(self.finish(self.model.slopes(x, z, cell)), reference)

    def test_linear_height_field_lod_blend_preserves_weighted_slope(self):
        # With no horizontal deformation, blending displacement height fields
        # gives the weighted sum of their partial derivatives exactly. General
        # chopped-wave LOD blending remains an approximation in the renderer.
        for a, b in (((2, -.5), (-1, 1.5)), ((.125, .25), (3, -2))):
            for weights in ((1, 0), (.75, .25), (.5, .5), (0, 1), (.3, .2), (0, 0)):
                actual_slopes = Vector((0, 0))
                for gradient, weight, cell in zip((a, b), weights, (.5, 1)):
                    actual_slopes += self.model.slopes((1, gradient[0], 0), (0, gradient[1], 1), cell, weight=weight)
                actual = self.finish(actual_slopes)
                expected_gradient = Vector(a) * weights[0] + Vector(b) * weights[1]
                self.assertAlmostEqual(dot(actual, (1, expected_gradient.x, 0)), 0, delta=2e-6)
                self.assertAlmostEqual(dot(actual, (0, expected_gradient.y, 1)), 0, delta=2e-6)

    def test_float_displacement_samples_keep_a_constant_surface_flat(self):
        for height in (.0312652587890625, 20.0078125, 100.015625, -100.015625):
            for cell in (.125, 1, 8):
                normal = self.finish(self.model.slopes((1, 0, 0), (0, 0, 1), cell, (0, height, 0)))
                self.assertVector(normal, (0, 1, 0), tolerance=1e-12)

    def test_collapsed_and_overturned_surfaces_have_finite_upward_fallback(self):
        for x, z in (((0, 0, 0), (0, 0, 0)), ((0, 1, 0), (0, 2, 0)),
                     ((0, 1, 0), (0, 0, 1)), ((-1, .5, 0), (0, -.25, 1))):
            for cell in (.001, 1, 64):
                normal = self.finish(self.model.slopes(x, z, cell))
                self.assertTrue(all(math.isfinite(value) for value in normal))
                self.assertGreater(normal.y, 0)
                self.assertAlmostEqual(length(normal), 1, places=12)

    def test_existing_overall_strength_control_retains_flat_and_full_endpoints(self):
        slopes = self.model.slopes((1, 2, 0), (0, -.5, 1))
        self.assertVector(self.finish(slopes, 0), (0, 1, 0))
        for strength in (.25, .5, 1):
            normal = self.finish(slopes, strength)
            self.assertAlmostEqual(dot(normal, (1, 2 * strength, 0)), 0, delta=2e-6)
            self.assertAlmostEqual(dot(normal, (0, -.5 * strength, 1)), 0, delta=2e-6)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref")
    args, remaining = parser.parse_known_args()
    source.SOURCE_REF = args.ref
    print("Wave normals: source expressions, sampled precision and surface geometry; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
