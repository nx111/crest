#!/usr/bin/env python3
"""Check the ocean's actual normal-composition chain against rotation geometry.

Inject effective texture XY samples into the existing sampling/Flow interfaces,
then evaluate the shader's assignments. Texture decoding/filtering, Unity import
and GPU execution are outside this test. Selected cases round normal arithmetic
to IEEE binary32; this is not an exact GPU/FMA/half-precision execution model.
Use --ref <commit> to evaluate the previous composition without editing files.
"""
import argparse
import math
import re
import struct
import sys
import unittest

import check_urp_regressions as source
from check_normal_sampling import calls
from check_optical_quality import Vector, dot, length, evaluate

NORMALS = source.CREST + "Shaders/OceanNormalMapping.hlsl"


def normalize(value):
    return Vector(value) / length(value)


def cross(a, b):
    return Vector((a[1] * b[2] - a[2] * b[1],
                   a[2] * b[0] - a[0] * b[2],
                   a[0] * b[1] - a[1] * b[0]))


def independent_rotation(base, detail):
    """Rodrigues' axis/angle rotation, independent of the shader's RNM identity."""
    local = normalize((detail[0], 1, detail[1]))
    axis = cross((0, 1, 0), base)
    sine = length(axis)
    if sine < 1e-15:
        return local
    axis /= sine
    cosine = base[1]
    return local * cosine + cross(axis, local) * sine + axis * (dot(axis, local) * (1 - cosine))


def f32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


class Float32Vector(Vector):
    def __new__(cls, values):
        return super().__new__(cls, (f32(value) for value in values))

    def operation(self, other, function):
        values = other if isinstance(other, tuple) else (other,) * len(self)
        return Float32Vector(function(a, b) for a, b in zip(self, values))

    def __getattr__(self, name):
        result = super().__getattr__(name)
        return Float32Vector(result) if isinstance(result, tuple) else result


def select_branches(text, normal_mapping, flow):
    keywords = {"_APPLYNORMALMAPPING_ON": normal_mapping, "_FLOW_ON": flow}
    active, stack, output = True, [], []
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("#if "):
            condition = keywords[line[4:].strip()]
            stack.append((active, condition))
            active = active and condition
        elif line == "#else":
            parent, condition = stack[-1]
            active = parent and not condition
        elif line == "#endif":
            active, _ = stack.pop()
        elif line.startswith("#"):
            raise AssertionError(f"Unsupported normal-chain directive: {line}")
        elif active:
            output.append(line)
    if stack:
        raise AssertionError("Unclosed normal-chain directive")
    return "\n".join(output)


class NormalChain:
    def __init__(self, binary32=False):
        self.ocean = source.source(source.OCEAN)
        self.normals = source.source(NORMALS)
        self.vector = Float32Vector if binary32 else Vector
        self.round = f32 if binary32 else float
        self.initial = re.search(r"float3 n_pixel\s*=\s*([^;]+);", self.ocean)[1]
        start = self.ocean.index("#endif", self.ocean.index("sss = saturate")) + len("#endif")
        end = self.ocean.index("float normalVariance", start)
        self.block = self.ocean[start:end]
        self.flip = re.search(r"if\s*\(underwater\)\s*n_pixel\s*=\s*([^;]+);", self.ocean)[1]
        self.flow_body = source.body(self.normals, "ApplyNormalMapsWithFlow")
        self.sample_return = re.search(r"return\s+([^;]+);", source.body(self.normals, "SampleNormalMaps"))[1]
        self.functions = {
            "float2": lambda *items: self.vector(items),
            "float3": lambda *items: self.vector(items),
            "dot": self.shader_dot,
            "normalize": lambda value: value / self.round(math.sqrt(self.shader_dot(value, value))),
            "fmod": math.fmod,
        }
        helpers = source.source(source.CREST + "Shaders/OceanHelpersNew.hlsl")
        if "float CrestFlowPhase(" in helpers:
            flow_helper = source.body(helpers, "CrestFlowPhase")
            self.functions["CrestFlowPhase"] = lambda time, period: self.run(
                flow_helper, {"time": time, "period": period})
        if "float3 CrestReorientNormal(" in self.normals:
            helper = source.body(self.normals, "CrestReorientNormal")
            self.functions["CrestReorientNormal"] = lambda base, detail: self.run(
                helper, {"i_baseNormalWS": base, "i_detailXZ": detail})

    def shader_dot(self, a, b):
        result = 0.0
        for x, y in zip(a, b):
            result = self.round(result + self.round(x * y))
        return result

    def equation(self, expression, values):
        expression = expression.replace("input.seaLevelDerivs", "seaLevelDerivs")
        expression = expression.replace("input.flow_shadow.xy", "flow")
        return evaluate(expression, values, self.functions)

    def assign(self, values, name, value):
        if "." not in name:
            values[name] = self.vector(value) if isinstance(value, tuple) else self.round(value)
            return
        variable, swizzle = name.split(".")
        result = list(values[variable])
        for component, item in zip(swizzle, value if isinstance(value, tuple) else (value,)):
            result["xyzw".index(component)] = item
        values[variable] = self.vector(result)

    def run(self, block, values):
        values = dict(values)
        for statement in block.split(";"):
            statement = statement.strip()
            if not statement:
                continue
            if statement.startswith("return "):
                return self.equation(statement[7:], values)
            conditional = re.fullmatch(r"if\s*\(([^()]*)\)\s*(.+)", statement, re.S)
            if conditional:
                if not self.equation(conditional[1], values):
                    continue
                statement = conditional[2]
            if statement.startswith("ApplyNormalMapsWithFlow("):
                arguments = calls(statement, "ApplyNormalMapsWithFlow")[0]
                names = ("i_texture", "worldXZUndisplaced", "flow", "lodAlpha", "cascadeData", "instanceData", "io_n")
                inputs = dict(zip(names, (self.equation(argument, values) for argument in arguments)))
                flowed = self.run(self.flow_body, {**values, **inputs})
                self.assign(values, arguments[-1], flowed["io_n"])
                continue
            match = re.fullmatch(r"(?:(?:const\s+)?(?:float|half)[234]?\s+)?"
                                 r"(\w+(?:\.[xyzw]+)?)\s*([+*\-]?=)\s*(.+)", statement, re.S)
            if match is None:
                raise AssertionError(f"Unsupported normal-chain statement: {statement}")
            name, operation, expression = match.groups()
            value = self.equation(expression, values)
            if operation != "=":
                previous = self.equation(name, values)
                value = previous + value if operation == "+=" else previous - value if operation == "-=" else previous * value
            self.assign(values, name, value)
        return values

    def compose(self, wave, detail=(0, 0), level=(0, 0), overall=1, strength=1,
                flow=False, normal_mapping=True, time=.25, underwater=False, sampler=None):
        values = {"_NormalsStrengthOverall": overall, "_NormalsStrength": strength, "_CrestTime": time,
                  "seaLevelDerivs": -self.vector(level), "flow": self.vector((.7, -.4)),
                  "_NormalsTiledTexture": None, "positionXZWSUndisplaced": self.vector((0, 0)),
                  "lodAlpha": .3, "cascadeData0": None, "instanceData": None}
        initial = self.equation(self.initial, values)
        values["n_pixel"] = self.vector((initial.x + wave[0], initial.y, initial.z + wave[1]))

        def sample(texture, world, offset, lod, cascade, instance):
            raw = sampler(offset) if sampler else detail
            return self.equation(self.sample_return, {**values, "norm": self.vector(raw)})

        self.functions["SampleNormalMaps"] = sample
        normal = self.run(select_branches(self.block, normal_mapping, flow), values)["n_pixel"]
        return self.equation(self.flip, {"n_pixel": normal}) if underwater else normal


class DetailNormals(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.chain = NormalChain()

    def assertVector(self, actual, expected, tolerance=2e-10):
        self.assertEqual(len(actual), len(expected))
        for a, e in zip(actual, expected):
            self.assertAlmostEqual(a, e, delta=tolerance)

    def test_zero_detail_preserves_wave_and_water_level_strength(self):
        for wave, level in (((0, 0), (0, 0)), ((2, -.5), (0, 0)), ((0, 0), (-.75, 1.5)), ((2, -.5), (.25, .75))):
            for overall in (0, .125, .5, 1):
                slopes = (Vector(wave) + Vector(level)) * overall
                expected = normalize((slopes.x, 1, slopes.y))
                for flow in (False, True):
                    for mapping in (False, True):
                        self.assertVector(self.chain.compose(wave, level=level, overall=overall,
                                                             flow=flow, normal_mapping=mapping), expected)

    def test_flat_macro_preserves_both_existing_strength_controls(self):
        for detail in ((0, 0), (.5, -.25), (-1.3, .75), (2, -2)):
            for strength in (0, .01, .36, 1, 2):
                for overall in (0, .125, .5, 1):
                    expected = normalize((detail[0] * strength * overall, 1, detail[1] * strength * overall))
                    for flow in (False, True):
                        self.assertVector(self.chain.compose((0, 0), detail, strength=strength, overall=overall, flow=flow), expected)

    def test_detail_angle_is_independent_of_macro_tilt_and_detail_azimuth(self):
        for tilt in (0, 10, 30, 60, 80, 89):
            for azimuth in (0, .6, 2.1, 4.7):
                wave = Vector((math.cos(azimuth), math.sin(azimuth))) * math.tan(math.radians(tilt))
                base = normalize((wave.x, 1, wave.y))
                for detail_angle in (0, .5, 1.7, 3.1, 4.8):
                    detail = Vector((math.cos(detail_angle), math.sin(detail_angle))) * .5
                    actual = self.chain.compose(wave, detail)
                    self.assertAlmostEqual(dot(actual, base), 1 / math.sqrt(1 + dot(detail, detail)), delta=2e-12)

    def test_opposite_details_have_equal_tilt_and_no_tangent_bias(self):
        for wave in ((math.sqrt(3), 0), (.75, -3), (-2, .5)):
            base = normalize((wave[0], 1, wave[1]))
            for detail in ((.5, 0), (0, .5), (.4, -.3)):
                plus = self.chain.compose(wave, detail)
                minus = self.chain.compose(wave, -Vector(detail))
                self.assertAlmostEqual(dot(plus, base), dot(minus, base), delta=1e-12)
                self.assertVector(normalize(plus + minus), base)

    def test_full_direction_matches_independent_axis_angle_rotation(self):
        for wave, level in (((.25, .5), (0, 0)), ((2, -1), (.25, .5)), ((-3, 4), (.5, -.25))):
            for overall in (.125, .5, 1):
                slopes = (Vector(wave) + Vector(level)) * overall
                base = normalize((slopes.x, 1, slopes.y))
                for detail in ((.5, 0), (-.25, .75), (-1, -.5)):
                    expected = independent_rotation(base, Vector(detail) * overall * .36)
                    actual = self.chain.compose(wave, detail, level, overall, strength=.36)
                    self.assertVector(actual, expected)

    def test_water_level_slope_participates_in_detail_orientation(self):
        for level in ((1, 0), (0, -2), (.75, 1.25)):
            base = normalize((level[0], 1, level[1]))
            for detail in ((.4, -.2), (-.5, .75)):
                expected = independent_rotation(base, detail)
                self.assertVector(self.chain.compose((0, 0), detail, level=level), expected)
                # The same macro slope has the same frame regardless of whether
                # it came from waves, water-level geometry, or both.
                self.assertVector(self.chain.compose(level, detail), expected)
                self.assertVector(self.chain.compose(Vector(level) * .3, detail, Vector(level) * .7), expected)

    def test_flow_blends_effective_details_before_one_reorientation(self):
        samples = (Vector((1, -.25)), Vector((-.5, .75)))
        wave, level, overall, strength = Vector((2, -1)), Vector((.25, .5)), .5, .36
        slopes = (wave + level) * overall
        base = normalize((slopes.x, 1, slopes.y))
        for time in (-7.6, -2, -1.75, -1.1, -1, -.9, -.25, 0, .25, .9, 1, 1.1, 1.75, 2, 7.6):
            weight = 1 - abs(time % 2 - 1)
            detail = (samples[0] * weight + samples[1] * (1 - weight)) * overall * strength
            iterator = iter(samples)
            actual = self.chain.compose(wave, level=level, overall=overall, strength=strength,
                                        flow=True, time=time, sampler=lambda offset: next(iterator))
            self.assertVector(actual, independent_rotation(base, detail))

    def test_flow_resets_remain_continuous_with_a_smooth_sample_field(self):
        # Synthetic values stand in for filtered texture samples. At a phase's
        # reset its weight must vanish, independent of the resetting sample.
        sampler = lambda offset: (math.sin(3 * offset[0] + .2), .7 * math.cos(2 * offset[1] - .4))
        for time in (-8, -3, -2, -1, 0, 1, 2, 3, 8):
            before = self.chain.compose((2, -.75), flow=True, time=time - 1e-7, sampler=sampler)
            after = self.chain.compose((2, -.75), flow=True, time=time + 1e-7, sampler=sampler)
            self.assertLess(length(before - after), 2e-6)

    def test_underwater_flips_the_complete_normal_after_variance(self):
        for overall in (0, .25, 1):
            for flow in (False, True):
                above = self.chain.compose((2, -.5), (.75, -.25), (.25, .5), overall, flow=flow)
                below = self.chain.compose((2, -.5), (.75, -.25), (.25, .5), overall, flow=flow, underwater=True)
                self.assertVector(below, -above)
                self.assertAlmostEqual(length(below), 1, places=12)
                if overall == 0:
                    self.assertVector(above, (0, 1, 0))
        variance = self.chain.ocean.index("float normalVariance")
        flip = self.chain.ocean.index("if (underwater) n_pixel = -n_pixel;")
        self.assertLess(variance, flip)
        self.assertIn("CrestSpecularNormalVariance(n_pixel)", self.chain.ocean[variance:flip])

    def test_binary32_near_horizontal_frames_keep_finite_outward_details(self):
        chain = NormalChain(binary32=True)
        negative_world_y = False
        for slope in (0, 1e-8, .5, 2, 100, 10000, 1e8):
            for azimuth in (0, .7, 3.4):
                wave = Vector((math.cos(azimuth), math.sin(azimuth))) * slope
                base = normalize((wave.x, 1, wave.y))
                for detail in ((0, 0), (.75, -.25), (-.5, 1), (4, -4)):
                    actual = chain.compose(wave, detail)
                    self.assertTrue(all(math.isfinite(value) for value in actual))
                    self.assertAlmostEqual(length(actual), 1, delta=3e-6)
                    self.assertGreater(dot(actual, base), 0)
                    self.assertVector(actual, independent_rotation(base, detail), tolerance=3e-6)
                    negative_world_y |= actual.y < 0
        # Do not turn the final shading normal back into an upward height-field
        # slope: that would destroy valid outward detail on a steep macro face.
        self.assertTrue(negative_world_y)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref")
    args, remaining = parser.parse_known_args()
    source.SOURCE_REF = args.ref
    print("Detail normals: actual composition/Flow expressions, rotation invariants and selected binary32 arithmetic; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
