#!/usr/bin/env python3
"""Foam transport and emitter regressions evaluated from actual source formulas.

The references are analytic trajectories, independent cascade footprints, and
the constant-source decay ODE. This uses double-precision CPU arithmetic, not
Unity, shader compilation, GPU texture filtering, or GPU performance testing.
--ref reads an earlier revision without modifying the working tree.
"""

import argparse
import ast
from dataclasses import dataclass
import math
import re
import sys
import unittest

import check_foam_quality as source_utils

ROOT = "crest/Assets/Crest/Crest/"
COMPUTE = ROOT + "Shaders/Resources/UpdateFoam.compute"
MANAGER = ROOT + "Scripts/LodData/LodDataMgrFoam.cs"
INPUTS = (
    ROOT + "Shaders/OceanInputs/FoamAddFromTex.shader",
    ROOT + "Shaders/OceanInputs/FoamAddFromVertCol.shader",
    ROOT + "Shaders/OceanInputs/Resources/FoamSplineGeometry.shader",
)


class Vec(tuple):
    def combine(self, other, operation):
        other = other if isinstance(other, tuple) else (other,) * len(self)
        return Vec(operation(a, b) for a, b in zip(self, other))

    def __add__(self, other):
        return self.combine(other, lambda a, b: a + b)

    __radd__ = __add__

    def __sub__(self, other):
        return self.combine(other, lambda a, b: a - b)

    def __mul__(self, other):
        return self.combine(other, lambda a, b: a * b)

    __rmul__ = __mul__

    def __truediv__(self, other):
        return self.combine(other, lambda a, b: a / b)

    def __abs__(self):
        return Vec(abs(item) for item in self)

    def __getattr__(self, name):
        if name and all(character in "xyz" for character in name):
            values = Vec(self["xyz".index(character)] for character in name)
            return values[0] if len(values) == 1 else values
        raise AttributeError(name)


def vector(*values):
    return Vec(item for value in values for item in (value if isinstance(value, tuple) else (value,)))


def expression(text, values):
    text = text.replace("&&", " and ").replace("||", " or ")
    text = text.replace("Mathf.Max", "max").replace("Mathf.Exp", "exp")
    text = re.sub(r"(?<=\d)f\b", "", text).strip()
    tree = ast.parse(text, mode="eval")
    context = dict(source_utils.MATH, float2=vector, float3=vector, **values)
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.BoolOp, ast.Call,
               ast.Name, ast.Load, ast.Constant, ast.Add, ast.Sub, ast.Mult,
               ast.Div, ast.USub, ast.Attribute, ast.Subscript, ast.Compare,
               ast.Gt, ast.Lt, ast.GtE, ast.LtE, ast.Eq, ast.NotEq, ast.And, ast.Or)
    attributes = {"x", "y", "z", "xy", "xz", "xyz", "_posSnapped", "_texelWidth", "_textureRes", "_oneOverTextureRes"}
    for node in ast.walk(tree):
        if not isinstance(node, allowed):
            raise AssertionError(f"Unsupported expression: {text}")
        if isinstance(node, ast.Call) and (not isinstance(node.func, ast.Name) or not callable(context.get(node.func.id))):
            raise AssertionError(f"Unsupported call: {text}")
        if isinstance(node, ast.Attribute) and node.attr not in attributes:
            raise AssertionError(f"Unsupported field: {text}")
    return eval(compile(tree, "<foam-dynamics-source>", "eval"), {"__builtins__": {}}, context)


def conditional(text):
    match = re.match(r"if\s*\(([^{}]+)\)\s*\{", text)
    yes, end = source_utils.braced(text, match.end() - 1)
    tail = text[end:]
    no = ""
    if tail.lstrip().startswith("else"):
        start = end + len(tail) - len(tail.lstrip()) + len("else")
        start += len(text[start:]) - len(text[start:].lstrip())
        if text[start:].startswith("if"):
            _, _, _, no_end = conditional(text[start:])
            no, end = text[start:start + no_end], start + no_end
        else:
            no, end = source_utils.braced(text, start)
    return match[1], yes, no, end


def execute(block, values):
    """Evaluate only the assignments, conditionals and bounded loops in scope."""
    block = re.sub(r"\[loop\]", "", block)
    block = re.sub(r"(\w+)\.SampleLevel\(LODData_linear_clamp_sampler,\s*([^,]+),\s*0(?:\.0)?\)", r"SampleLod(\1, \2)", block)
    while block.strip():
        block = block.lstrip()
        if block.startswith("if"):
            check, yes, no, end = conditional(block)
            returned, value = execute(yes if expression(check, values) else no, values)
            if returned:
                return True, value
            block = block[end:]
            continue
        loop = re.match(r"for\s*\(uint\s+(\w+)\s*=\s*([^;]+);\s*([^;]+);\s*\1\+\+\)\s*\{", block)
        if loop:
            inner, end = source_utils.braced(block, loop.end() - 1)
            values[loop[1]] = expression(loop[2], values)
            iterations = 0
            while expression(loop[3], values):
                if iterations >= 32:
                    raise AssertionError("Cascade iteration did not terminate")
                returned, value = execute(inner, values)
                if returned:
                    return True, value
                values[loop[1]] += 1
                iterations += 1
            block = block[end:]
            continue
        statement, block = block.split(";", 1)
        statement = statement.strip()
        if statement.startswith("return "):
            return True, expression(statement[7:], values)
        statement = re.sub(r"^(?:const\s+)?(?:float[234]?|half[234]?|uint|var|CascadeParams)\s+", "", statement)
        match = re.fullmatch(r"(\w+)\s*([+*/-]?=)\s*(.+)", statement, flags=re.S)
        if match is None:
            raise AssertionError(f"Unsupported statement: {statement}")
        value = expression(match[3], values)
        values[match[1]] = value if match[2] == "=" else expression(f"previous {match[2][0]} nextValue", {"previous": values[match[1]], "nextValue": value})
    return False, None


@dataclass
class Cascade:
    _texelWidth: float
    _posSnapped: Vec = Vec((0.0, 0.0))
    _textureRes: int = 32

    @property
    def _oneOverTextureRes(self):
        return 1.0 / self._textureRes

    def world(self, uv):
        # Independent geometric definition of the square capture footprint.
        return self._posSnapped + (uv.xy - 0.5) * (self._texelWidth * self._textureRes)

    def contains(self, position):
        half_extent = (self._textureRes / 2.0 - 1.0) * self._texelWidth
        delta = position - self._posSnapped
        return abs(delta.x) <= half_extent and abs(delta.y) <= half_extent


class FoamDynamicsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.compute = source_utils.source(COMPUTE)
        cls.manager = source_utils.source(MANAGER)
        cls.inputs = [source_utils.source(path) for path in INPUTS]
        helpers = source_utils.source(ROOT + "Shaders/OceanHelpersNew.hlsl")
        signature = re.search(r"float3 WorldToUV\([^{}]+\)\s*\{", helpers)
        cls.world_to_uv_body = source_utils.braced(helpers, signature.end() - 1)[0]

    def call(self, source, name, values):
        return execute(source_utils.body(source, name), values)[1]

    def transport_context(self, cascades, field, source_cascades=None):
        source_cascades = source_cascades or cascades
        sampled_flow, sampled_history = [], []

        def uv(position, cascade, index):
            return execute(self.world_to_uv_body, {"i_samplePos": position, "i_cascadeParams": cascade, "i_sliceIndex": index})[1]

        def flow(coordinates):
            index = int(coordinates.z)
            sampled_flow.append(index)
            return vector(field(cascades[index].world(coordinates)), 0.0)

        def history(coordinates):
            index = int(coordinates.z)
            sampled_history.append(index)
            # A nonzero, distinct constant in each source cascade exposes which
            # data was selected, without introducing texture interpolation error.
            return Vec(((index + 1) / 10.0, 0.0, 0.0))

        values = {"_CrestCascadeData": cascades, "_CrestCascadeDataSource": source_cascades,
                  "_LD_TexArray_Flow": flow, "_LD_TexArray_Foam_Source": history,
                  "SampleLod": lambda texture, coordinates: texture(coordinates), "WorldToUV": uv}
        if "CrestFoamIsInCascade" in self.compute:
            values["CrestFoamIsInCascade"] = lambda uv_, inverse: self.call(self.compute, "CrestFoamIsInCascade", {"uv": uv_, "oneOverResolution": inverse})
            values["CrestFoamSampleFlow"] = lambda position, first, count, fallback: self.call(self.compute, "CrestFoamSampleFlow", dict(values, positionXZ=position, firstSlice=first, sliceCount=count, fallbackVelocity=fallback))
        return values, sampled_flow, sampled_history

    def backtrace(self, position, dt, field, cascades=None):
        cascades = cascades or [Cascade(128.0)]
        values, flow_samples, _ = self.transport_context(cascades, field)
        if "CrestFoamBacktrace" in self.compute:
            result = self.call(self.compute, "CrestFoamBacktrace", dict(values, positionXZ=position, deltaTime=dt, slice=0, sliceCount=len(cascades)))
        else:
            # Read and execute the original Euler trajectory for --ref.
            block = self.compute[self.compute.index("const half2 velocity ="):self.compute.index("const half r_max =")]
            values.update(worldPosXZ=position, _SimDeltaTime=dt, uv_slice=values["WorldToUV"](position, cascades[0], 0))
            execute(block, values)
            result = values["worldPosXZ_flowed"]
        return result, flow_samples

    def history(self, position, first, count, cascades):
        values, _, samples = self.transport_context(cascades, lambda p: Vec((0.0, 0.0)))
        if "CrestFoamSampleHistory" in self.compute:
            result = self.call(self.compute, "CrestFoamSampleHistory", dict(values, positionXZ=position, firstSlice=first, sliceCount=count))
        else:
            start = self.compute.index("const half2 velocity =")
            block = source_utils.braced(self.compute, self.compute.rfind("{", 0, start))[0]
            values.update(worldPosXZ=position, _SimDeltaTime=0.0, sliceIndexSource=first, depth=count, foam=0.0,
                          uv_slice=values["WorldToUV"](position, cascades[0], 0))
            execute(block, values)
            result = values["foam"]
        return result, samples

    def input_scale(self, dt, rate):
        if "CalculateFoamInputScale" not in self.manager:
            # The old manager submits fixed deposits only for positive substeps.
            return 1.0 if dt > 0.0 else 0.0
        integrated = lambda duration, fade: self.call(self.manager, "IntegratedSourceWeight", {"deltaTime": duration, "fadeRate": fade})
        return self.call(self.manager, "CalculateFoamInputScale", {"deltaTime": dt, "fadeRate": rate, "IntegratedSourceWeight": integrated})

    def input_deposit(self, index, scale, strength=0.1):
        text = self.inputs[index]
        signature = re.search(r"Frag\([^{}]*\)\s*:\s*SV_Target\s*\{", text)
        block = source_utils.braced(text, signature.end() - 1)[0]
        returned = re.findall(r"return ([^;]+);", block)[-1]
        returned = re.sub(r"UNITY_SAMPLE_TEX2D\([^)]*\)", "textureValue", returned)
        returned = returned.replace("input.col", "vertexColor")
        return expression(returned, {"_CrestFoamInputScale": scale, "_Strength": strength,
                                     "textureValue": 0.25, "vertexColor": Vec((0.25, 0.0, 0.0)), "wt": strength})

    def test_wave_filter_does_not_move_shoreline_sampling(self):
        end = self.compute.index("const float3 uv_slice_displaced =")
        start = self.compute.index("foam += 5.0 *")
        section = self.compute[self.compute.index(";", start) + 1:end]
        position_expression = re.search(r"uv_slice_displaced = WorldToUV\(([^,]+),", self.compute)[1]
        depth_expression = re.search(r"signedOceanDepth = ([^;]+);", self.compute)[1]
        slice_expression = re.search(r"wavesSliceIndex = ([^;]+);", self.compute)[1].replace("id.z", "targetSlice")
        displacements = (Vec((3.0, 0.5, -1.0)), Vec((1.0, 0.25, 0.5)), Vec((0.25, -0.5, 1.0)))
        for target_slice in range(3):
            for minimum_slice in range(3):
                selected = int(expression(slice_expression, {"_CrestMinimumWavesSlice": minimum_slice, "targetSlice": target_slice, "depth": 3.0}))
                actual_displacement = displacements[target_slice]
                values = {"wavesSliceIndex": selected, "sliceIndex": target_slice, "disp": displacements[selected],
                          "uv_slice": target_slice, "_LD_TexArray_AnimatedWaves": displacements,
                          "SampleLod": lambda data, index: data[index], "worldPosXZ": Vec((2.0, -3.0))}
                execute(section, values)
                position = expression(position_expression, values)
                expected_position = values["worldPosXZ"] + actual_displacement.xz
                self.assertEqual(position, expected_position, (target_slice, minimum_slice))
                # A sloping seabed is queried at the displaced surface location.
                values.update(_OceanCenterPosWorld=Vec((0.0, 10.0, 0.0)),
                              terrainHeight_seaLevelOffset=Vec((9.0 + position.x * 0.1 + position.y * 0.2, 0.0)))
                depth = expression(depth_expression, values)
                expected_depth = 1.0 + actual_displacement.y - expected_position.x * 0.1 - expected_position.y * 0.2
                self.assertAlmostEqual(depth, expected_depth, delta=1e-12)

    def test_whitecap_filter_still_selects_requested_active_waves(self):
        value = re.search(r"wavesSliceIndex = ([^;]+);", self.compute)[1].replace("id.z", "targetSlice")
        for count in (2, 4, 8):
            for target in range(count):
                for minimum in (0, 1, count - 1, count + 3):
                    actual = expression(value, {"_CrestMinimumWavesSlice": minimum, "targetSlice": target, "depth": count})
                    self.assertEqual(actual, min(max(minimum, target), count - 1))

    def test_uniform_flow_translates_without_changing_speed(self):
        for dt in (0.0, 1 / 200, 1 / 30, 1 / 15, 0.2):
            for velocity in (Vec((0.0, 0.0)), Vec((3.0, -4.0)), Vec((-120.0, 80.0))):
                origin = Vec((12.0, -5.0))
                actual, _ = self.backtrace(origin, dt, lambda p: velocity)
                self.assertEqual(actual, origin - dt * velocity)

    def test_rotational_flow_has_second_order_trajectory_accuracy(self):
        radius = 10.0
        errors = []
        for dt in (0.2, 0.1, 0.05):
            actual, _ = self.backtrace(Vec((radius, 0.0)), dt, lambda p: Vec((-p.y, p.x)))
            expected = Vec((radius * math.cos(dt), -radius * math.sin(dt)))
            error = math.dist(actual, expected)
            self.assertLess(error, radius * dt ** 3 / 5.9)
            errors.append(error)
        self.assertGreater(errors[0] / errors[1], 7.9)
        self.assertGreater(errors[1] / errors[2], 7.9)

    def test_rotating_wake_radius_resists_numerical_contraction(self):
        radius = 10.0
        for frequency in (15, 30, 60):
            mapped = Vec((radius, 0.0))
            for _ in range(2 * frequency):
                mapped, _ = self.backtrace(mapped, 1.0 / frequency, lambda p: Vec((-p.y, p.x)))
            # For this linear flow the inverse sampling map scales all radii
            # equally. The rendered ring has the reciprocal scale factor.
            transported_radius = radius * radius / math.hypot(*mapped)
            self.assertLess(abs(transported_radius - radius), radius * 0.0001)

    def test_zero_flow_and_zero_time_skip_extra_midpoint_lookup(self):
        for dt, velocity in ((0.0, Vec((3.0, -2.0))), (1 / 30, Vec((0.0, 0.0)))):
            origin = Vec((1.0, 2.0))
            actual, samples = self.backtrace(origin, dt, lambda p: velocity)
            self.assertEqual(actual, origin)
            self.assertEqual(len(samples), 1)

    def test_history_search_reaches_the_nearest_valid_ancestor(self):
        cascades = [Cascade(2.0 ** index) for index in range(5)]
        for position in (Vec((40.0, 5.0)), Vec((-80.0, -2.0)), Vec((1.0, 180.0))):
            for first in (0, 1):
                selected = next(index for index in range(first, len(cascades)) if cascades[index].contains(position))
                foam, samples = self.history(position, first, len(cascades), cascades)
                self.assertEqual(foam, (selected + 1) / 10.0)
                self.assertEqual(samples, [selected])

    def test_history_never_reads_inactive_or_clamped_edge_data(self):
        cascades = [Cascade(2.0 ** index) for index in range(6)]
        for count in (1, 2, 4):
            point = Vec((cascades[count - 1]._texelWidth * cascades[count - 1]._textureRes, 0.0))
            foam, samples = self.history(point, 0, count, cascades)
            self.assertEqual(foam, 0.0)
            self.assertEqual(samples, [])

    def test_midpoint_flow_uses_valid_ancestors_and_exits_without_stretching(self):
        cascades = [Cascade(2.0 ** index) for index in range(4)]
        origin = Vec((10.0, 0.0))
        actual, sampled = self.backtrace(origin, 1.0, lambda p: Vec((20.0, 80.0 + p.x)), cascades)
        # This linear shear with constant X velocity has an exact quadratic
        # trajectory; its midpoint at (0, -45) needs the third cascade.
        self.assertEqual(actual, Vec((-10.0, -80.0)))
        self.assertEqual(sampled, [0, 2])
        actual, sampled = self.backtrace(origin, 1.0, lambda p: Vec((1000.0, 0.0)), cascades)
        self.assertEqual(actual, Vec((-990.0, 0.0)))
        self.assertEqual(sampled, [0])
        foam, _ = self.history(actual, 0, len(cascades), cascades)
        self.assertEqual(foam, 0.0)

    def test_default_30hz_preserves_each_authored_deposit(self):
        for rate in (0.0, 1e-8, 0.8, 5.0, 20.0):
            scale = self.input_scale(1 / 30, rate)
            self.assertEqual(scale, 1.0)
            for index, expected in enumerate((0.025, 0.025, 0.002)):
                self.assertAlmostEqual(self.input_deposit(index, scale), expected, delta=1e-14)

    def test_stationary_emitters_match_across_simulation_frequencies(self):
        for rate in (0.0, 1e-8, 0.8, 5.0, 20.0):
            reference_weight = -math.expm1(-rate / 30) / rate if rate else 1 / 30
            duration_weight = -math.expm1(-rate * 2) / rate if rate else 2.0
            for index in range(3):
                deposit = self.input_deposit(index, 1.0, strength=0.0004)
                expected = 0.2 * math.exp(-rate * 2) + deposit / reference_weight * duration_weight
                for frequency in (15, 30, 60, 200):
                    dt = 1.0 / frequency
                    addition = self.input_deposit(index, self.input_scale(dt, rate), strength=0.0004)
                    foam = 0.2
                    for _ in range(2 * frequency):
                        foam = foam * math.exp(-rate * dt) + addition
                    self.assertAlmostEqual(foam, expected, delta=1e-10, msg=(index, rate, frequency))

    def test_zero_step_and_small_fade_are_finite_and_preserve_input_sign(self):
        for dt in (0.0, 1 / 200, 1 / 30, 1 / 15):
            for rate in (-1.0, 0.0, 1e-12, 1e-8, 0.0001, 20.0):
                scale = self.input_scale(dt, rate)
                self.assertTrue(math.isfinite(scale) and scale >= 0.0)
                if dt == 0.0:
                    self.assertEqual(scale, 0.0)
                if rate <= 0.0:
                    self.assertAlmostEqual(scale, 30.0 * dt, delta=1e-12)
                for index in range(3):
                    negative = self.input_deposit(index, scale, strength=-0.2)
                    self.assertLessEqual(negative, 0.0)
                    self.assertEqual(negative, -self.input_deposit(index, scale, strength=0.2))

    def test_override_still_writes_an_absolute_value(self):
        text = source_utils.source(ROOT + "Shaders/OceanInputs/FoamOverride.shader")
        returned = re.findall(r"return ([^;]+);", text)[-1]
        for frequency in (15, 30, 200):
            for value in (0.0, 0.25, 1.0):
                self.assertEqual(expression(returned, {"_FoamValue": value, "_CrestFoamInputScale": self.input_scale(1 / frequency, 0.8)}), value)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read source files from this Git revision")
    args, remaining = parser.parse_known_args()
    source_utils.SOURCE_REF = args.ref
    print("Foam dynamics: source-expression CPU regressions; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
