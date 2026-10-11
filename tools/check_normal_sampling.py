#!/usr/bin/env python3
"""Normal-map footprint checks using shader expressions and independent affine geometry.

These are CPU numerical/source checks, not Unity import, sampler emulation or GPU
quality measurements. Use --ref <commit> to check a previous shader revision.
"""
import argparse
import ast
import math
import re
import sys
import unittest

import check_urp_regressions as regression
from check_flow_sampling import flow_phase

NORMALS = regression.CREST + "Shaders/OceanNormalMapping.hlsl"
TEXTURE = regression.CREST + "Shaders/ShaderLibrary/Texture.hlsl"
IMPORTER = regression.CREST + "Textures/WaveNormals/WaveNormals.png.meta"


class Vector(tuple):
    def __add__(self, other):
        if isinstance(other, Quad): return NotImplemented
        return Vector(a + b for a, b in zip(self, other))

    def __sub__(self, other):
        if isinstance(other, Quad): return NotImplemented
        return Vector(a - b for a, b in zip(self, other))

    def __mul__(self, scale): return Vector(value * scale for value in self)
    __rmul__ = __mul__
    def __truediv__(self, scale): return self * (1 / scale)
    def __neg__(self): return self * -1


class Quad:
    """Four neighbouring values; finite differences are exact for our affine cases."""
    def __init__(self, values): self.values = tuple(values)

    def operation(self, other, function):
        values = other.values if isinstance(other, Quad) else (other,) * 4
        return Quad(function(a, b) for a, b in zip(self.values, values))

    def __add__(self, other): return self.operation(other, lambda a, b: a + b)
    __radd__ = __add__
    def __sub__(self, other): return self.operation(other, lambda a, b: a - b)
    def __mul__(self, other): return self.operation(other, lambda a, b: a * b)
    __rmul__ = __mul__
    def __truediv__(self, other): return self.operation(other, lambda a, b: a / b)


def evaluate(expression, values):
    expression = expression.replace("i_texture._scale", "textureScale").replace("cascadeData._scale", "cascadeScale")
    functions = {
        "ddx": lambda quad: quad.values[1] - quad.values[0],
        "ddy": lambda quad: quad.values[2] - quad.values[0],
        "fmod": math.fmod,
        "CrestFlowPhase": flow_phase,
    }
    tree = ast.parse(expression.strip(), mode="eval")
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Call, ast.Name, ast.Load,
               ast.Constant, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.USub)
    for node in ast.walk(tree):
        if not isinstance(node, allowed): raise AssertionError(f"Unsupported shader expression: {expression}")
        if isinstance(node, ast.Call) and (not isinstance(node.func, ast.Name) or node.func.id not in functions):
            raise AssertionError(f"Unsupported shader call: {expression}")
    return eval(compile(tree, "<normal-sampling-expression>", "eval"), {"__builtins__": {}}, {**functions, **values})


def assignment(source, name):
    match = re.search(r"\b" + re.escape(name) + r"\s*=\s*([^;]+);", source)
    if match is None: raise AssertionError(f"Missing shader assignment: {name}")
    return match[1]


def calls(source, name):
    """Read all arguments, including parenthesised arithmetic, from actual calls."""
    result = []
    for match in re.finditer(re.escape(name) + r"\s*\(", source):
        depth, start, arguments = 0, match.end(), []
        for index in range(start, len(source)):
            character = source[index]
            if character == "(": depth += 1
            elif character == ")":
                if depth == 0:
                    arguments.append(source[start:index].strip())
                    result.append(arguments)
                    break
                depth -= 1
            elif character == "," and depth == 0:
                arguments.append(source[start:index].strip())
                start = index + 1
    return result


def matrix_vector(matrix, vector):
    return Vector(sum(a * b for a, b in zip(row, vector)) for row in matrix)


class NormalSampling(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.source = regression.source(NORMALS)
        cls.sampling = regression.body(cls.source, "SampleNormalMaps")
        cls.flow = regression.body(cls.source, "ApplyNormalMapsWithFlow")

    def footprint(self, world, offset, texture_scale, cascade_scale):
        prefix = self.sampling.split("const float spdmulL", 1)[0]
        # A shifting-origin correction is uniform within the draw. Test its
        # translation invariance separately rather than emulating its modulo.
        prefix = re.sub(r"#if CREST_FLOATING_ORIGIN.*?#endif", "", prefix, flags=re.S)
        values = {"i_worldXZUndisplaced": world, "offset": offset,
                  "textureScale": texture_scale, "cascadeScale": cascade_scale}
        values["worldXZUndisplaced"] = evaluate(assignment(prefix, "worldXZUndisplaced"), values)
        for expression in re.findall(r"worldXZUndisplaced\s*-=\s*([^;]+);", prefix):
            values["worldXZUndisplaced"] -= evaluate(expression, values)
        for name in ("nstretch", "normalUV", "normalUVdx", "normalUVdy"):
            values[name] = evaluate(assignment(prefix, name), values)
        return values

    def assertVector(self, actual, expected, tolerance=1e-10):
        for a, e in zip(actual, expected): self.assertAlmostEqual(a, e, delta=tolerance * max(1, abs(e)))

    def test_gradients_precede_varying_lod_branch_and_samples_are_explicit(self):
        branch = self.sampling.index("if (nblend > 0.001)")
        self.assertIn("ddx(", self.sampling)
        self.assertIn("ddy(", self.sampling)
        self.assertLess(self.sampling.index("ddx("), branch)
        self.assertLess(self.sampling.index("ddy("), branch)
        self.assertNotRegex(self.sampling[branch:], r"\b(?:ddx|ddy)\s*\(")
        self.assertNotRegex(self.sampling, r"i_texture\.Sample\s*\(")
        self.assertEqual(len(calls(self.sampling[:branch], "i_texture.SampleGrad")), 2)
        self.assertEqual(len(calls(self.sampling[branch:], "i_texture.SampleGrad")), 2)
        texture = regression.source(TEXTURE)
        self.assertIn("SAMPLE_TEXTURE2D_GRAD(_texture, _sampler, uv, gradientX, gradientY)", texture)

    def test_complete_affine_flow_footprint_matches_independent_jacobian(self):
        # The analytic derivative of P - t*F(P) is (I - t*A)*dP for F(P)=A*P+b.
        # Shader ddx/ddy instead observe neighbouring values after the flow offset.
        origin = Vector((11.0, -7.0))
        velocity = Vector((2.5, -.75))
        matrices = (((0, 0), (0, 0)), ((.4, -.7), (.2, .3)), ((-1.2, .6), (.8, -.9)))
        steps = ((Vector((.02, 0)), Vector((0, .1))),
                 (Vector((.04, .01)), Vector((-.03, .07))))
        for matrix in matrices:
            for dx, dy in steps:
                world = Quad((origin, origin + dx, origin + dy, origin + dx + dy))
                flow = Quad(velocity + matrix_vector(matrix, point - origin) for point in world.values)
                for texture_scale, cascade_scale in ((.01, 1), (40, .5), (200, 64)):
                    for phase in (-1, -.65, 0, .4, .999):
                        values = self.footprint(world, flow * phase, texture_scale, cascade_scale)
                        stretch = texture_scale * cascade_scale / 100
                        self.assertVector(values["normalUVdx"], (dx - matrix_vector(matrix, dx) * phase) / stretch)
                        self.assertVector(values["normalUVdy"], (dy - matrix_vector(matrix, dy) * phase) / stretch)

    def test_each_scroll_sample_uses_the_footprint_of_its_actual_uv(self):
        world = Quad(Vector((2 + .07 * x + .03 * y, -3 - .02 * x + .11 * y))
                     for x, y in ((0, 0), (1, 0), (0, 1), (1, 1)))
        flow = Quad(matrix_vector(((.5, -.2), (.1, .4)), point) for point in world.values)
        sample_calls = calls(self.sampling, "i_texture.SampleGrad")
        self.assertEqual(len(sample_calls), 4)
        flow_calls = calls(self.flow, "SampleNormalMaps")
        self.assertEqual(len(flow_calls), 2)
        for time in (-9.4, -1.001, -.999, -.3, 0, .3, .999, 1.001, 1.8, 9.4):
            phases = {"_CrestTime": time, "flow": flow}
            for name in ("half_period", "period", "sample1_offset", "sample2_offset"):
                phases[name] = evaluate(assignment(self.flow, name), phases)
            for name in ("sample1_offset", "sample2_offset"):
                expression = re.search(r"\b" + name + r"\s*-=\s*([^;]+);", self.flow)[1]
                phases[name] -= evaluate(expression, phases)
            for flow_call in flow_calls:
                offset = evaluate(flow_call[2], phases)
                values = self.footprint(world, offset, 40, 4)
                values.update(v0=Vector((.94, .34)), v1=Vector((-.85, -.53)), _CrestTime=time, spdmulL=.7, spdmulH=1.6)
                for index, arguments in enumerate(sample_calls):
                    if index == 2:
                        expression = re.search(r"nstretch\s*\*=\s*([^;]+);", self.sampling)[1]
                        values["nstretch"] *= evaluate(expression, values)
                    uv, dx, dy = (evaluate(argument, values) for argument in arguments)
                    self.assertVector(dx, uv.values[1] - uv.values[0])
                    self.assertVector(dy, uv.values[2] - uv.values[0])

    def test_far_cascade_doubles_world_period_and_halves_texture_footprint(self):
        world = Quad((Vector((0, 0)), Vector((.2, .03)), Vector((-.1, .5)), Vector((.1, .53))))
        values = self.footprint(world, Vector((0, 0)), 40, 2)
        sample_calls = calls(self.sampling, "i_texture.SampleGrad")
        self.assertEqual(len(sample_calls), 4)
        factor = evaluate(re.search(r"nstretch\s*\*=\s*([^;]+);", self.sampling)[1], values)
        self.assertEqual(factor, 2)
        for index in (0, 1):
            near = [evaluate(argument, values) for argument in sample_calls[index][1:]]
            far = [evaluate(argument, values) for argument in sample_calls[index + 2][1:]]
            for near_gradient, far_gradient in zip(near, far):
                self.assertVector(far_gradient, near_gradient / 2)
            for size in ((64, 64), (1024, 1024), (2048, 512)):
                # Texel footprint covers rectangular textures too: SampleGrad's
                # sampler uses both dimensions, without a square-texture mip estimate.
                footprint = lambda gradients: math.sqrt(max(sum((g * s) ** 2 for g, s in zip(gradient, size)) for gradient in gradients))
                self.assertAlmostEqual(math.log2(footprint(far)), math.log2(footprint(near)) - 1)

    def test_uniform_origin_and_flow_translations_do_not_change_footprint(self):
        world = Quad((Vector((0, 0)), Vector((.125, 0)), Vector((0, .5)), Vector((.125, .5))))
        expected = self.footprint(world, Vector((.25, .5)), 40, 8)
        for translation in (Vector((4000, -8000)), Vector((100000, 200000))):
            actual = self.footprint(world + translation, Vector((.25, .5)) + translation, 40, 8)
            self.assertVector(actual["normalUVdx"], expected["normalUVdx"])
            self.assertVector(actual["normalUVdy"], expected["normalUVdy"])

    def test_shipped_normal_importer_has_trilinear_mips_and_bounded_anisotropy(self):
        importer = regression.source(IMPORTER)
        properties = {name: int(re.search(r"^\s*" + name + r":\s*(-?\d+)\s*$", importer, re.M)[1])
                      for name in ("filterMode", "aniso", "enableMipMap", "sRGBTexture", "textureType")}
        self.assertEqual(properties, {"filterMode": 2, "aniso": 4, "enableMipMap": 1, "sRGBTexture": 0, "textureType": 1})


if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--ref")
    arguments, remaining = parser.parse_known_args()
    regression.SOURCE_REF = arguments.ref
    print("Normal sampling: shader expressions and affine footprint checks; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
