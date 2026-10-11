#!/usr/bin/env python3
"""Baked FFT query contracts, using actual C# expressions and periodic half data.

The restricted interpreter follows one SIMD lane, rounds float arithmetic to
binary32, and reads a real flattened four-channel binary16 texture fixture. The
references use independent periodic bilinear sampling and analytic affine
inversion. This is not Unity/Burst execution or a GPU/CPU rendering comparison.
"""

import argparse
import ast
from functools import lru_cache
import math
import re
import struct
import sys
from types import SimpleNamespace
import unittest

import check_urp_regressions as regression
from check_normal_sampling import calls
from check_wave_spectrum import balanced_end

DATA = regression.CREST + "Scripts/Shapes/FFT/FFTBakedData.cs"
PROVIDER = regression.CREST + "Scripts/Collision/CollProviderBakedFFT.cs"


class Float32(float):
    def __new__(cls, value):
        return super().__new__(cls, struct.unpack("f", struct.pack("f", value))[0])

    def __add__(self, other): return Float32(float(self) + float(other))
    __radd__ = __add__
    def __sub__(self, other): return Float32(float(self) - float(other))
    def __rsub__(self, other): return Float32(float(other) - float(self))
    def __mul__(self, other): return Float32(float(self) * float(other))
    __rmul__ = __mul__
    def __truediv__(self, other): return Float32(float(self) / float(other))
    def __rtruediv__(self, other): return Float32(float(other) / float(self))
    def __mod__(self, other): return Float32(math.fmod(float(self), float(other)))
    def __abs__(self): return Float32(abs(float(self)))
    def __neg__(self): return Float32(-float(self))


def half(value):
    return Float32(struct.unpack("e", struct.pack("e", value))[0])


def lerp(a, b, weight):
    # Unity.Mathematics uses x + s * (y - x). No fused instruction is assumed.
    return a + weight * (b - a)


MATH = {"fp": Float32, "integer": int, "absolute": abs,
        "floor": lambda x: Float32(math.floor(x)),
        "frac": lambda x: x - Float32(math.floor(x)),
        "select": lambda a, b, condition: b if condition else a,
        "lerp": lerp, "SpatialInterpolationData": SimpleNamespace}


@lru_cache(maxsize=None)
def compile_expression(text):
    text = re.sub(r"(?<=\d)f\b", "", text.strip())
    text = text.replace("math.abs", "absolute").replace("math.", "")
    text = text.replace("float4.zero", "fp(0.0)")
    text = text.replace("new SpatialInterpolationData()", "SpatialInterpolationData()")
    text = re.sub(r"\bin\s+", "", text)
    text = re.sub(r"\(int4\)\s*\(([^()]+)\)", r"integer(\1)", text)
    text = re.sub(r"\(int4\)\s*(\w+)", r"integer(\1)", text)
    tree = ast.parse(text, mode="eval")
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Call, ast.Name, ast.Load,
               ast.Constant, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.Mod,
               ast.LShift, ast.USub, ast.Attribute, ast.Subscript, ast.Compare,
               ast.Lt, ast.LtE, ast.Gt, ast.GtE, ast.Eq)
    for node in ast.walk(tree):
        if not isinstance(node, allowed):
            raise AssertionError(f"Unsupported C# sampling expression: {text}")
        if isinstance(node, ast.Call) and not isinstance(node.func, ast.Name):
            raise AssertionError(f"Unsupported call: {text}")
        if isinstance(node, ast.Attribute) and not re.fullmatch(r"_(?:period|frameCount|textureResolution|firstLod|lodCount|alpha[UV]|[UV][01])", node.attr):
            raise AssertionError(f"Unsupported field: {text}")

    class FloatConstants(ast.NodeTransformer):
        def visit_Constant(self, node):
            if isinstance(node.value, float):
                return ast.copy_location(ast.Call(ast.Name("fp", ast.Load()), [node], []), node)
            return node

    return compile(ast.fix_missing_locations(FloatConstants().visit(tree)), "<fft-query-source>", "eval")


class SourceSampler:
    """Execute only the static sampling functions, not Unity object lifecycle."""

    def __init__(self):
        self.text = regression.source(DATA)
        self.functions = {}
        for declaration in re.finditer(r"(?m)^\s*(?:(?:public|private|static)\s+)+"
                                       r"(?:void|float4)\s+(\w+)\s*\(([^{}]*?)\)\s*\{", self.text):
            name, parameters = declaration[1], declaration[2]
            if not (name.startswith("Sample") or name.startswith("Calculate") or name == "InterpolateData"):
                continue
            body = self.text[declaration.end():balanced_end(self.text, declaration.end() - 1, "{", "}")]
            self.functions[name] = ([(part.strip().split()[-1], part.strip().startswith("out "))
                                      for part in parameters.split(",")], body)
        self.constants = {name: int(re.search(r"\b" + name + r"\s*=\s*(\d+)", self.text)[1])
                          for name in ("kIterationCount", "kFloatsPerPoint")}
        self.spatial_samples = 0

    def evaluate(self, expression, values):
        functions = {name: (lambda *args, name=name: self.run(name, args)[0]) for name in self.functions}

        def element(array, index):
            # NativeArray does not allow Python's negative-index shorthand.
            if not 0 <= index < len(array):
                raise AssertionError(f"Baked array index {index} outside [0,{len(array)})")
            return array[index]

        context = dict(MATH, **functions, ElementsAt=element)
        return eval(compile_expression(expression), {"__builtins__": {}}, dict(context, **values))

    @staticmethod
    def assign(target, value, values):
        if "." in target:
            owner, field = target.split(".", 1)
            setattr(values[owner], field, value)
        elif target != "_":
            values[target] = value

    def execute(self, body, values):
        while body.strip():
            body = body.lstrip()
            loop = re.match(r"for\s*\((?:int|var)\s+(\w+)\s*=\s*([^;]+);\s*([^;]+);\s*\1\+\+\)\s*\{", body)
            if loop:
                end = balanced_end(body, loop.end() - 1, "{", "}")
                values[loop[1]] = self.evaluate(loop[2], values)
                count = 0
                while self.evaluate(loop[3], values):
                    if count >= 32: raise AssertionError("Unbounded sampling loop")
                    self.execute(body[loop.end():end], values)
                    values[loop[1]] += 1
                    count += 1
                body = body[end + 1:]
                continue
            statement, body = body.split(";", 1)
            statement = statement.strip()
            if statement.startswith("return "):
                return self.evaluate(statement[7:], values)
            invocation = re.match(r"(\w+)\s*\(", statement)
            if invocation and invocation[1] in self.functions:
                name = invocation[1]
                arguments, = calls(statement, name)
                parameters, _ = self.functions[name]
                evaluated, destinations = [], []
                for (parameter, output), argument in zip(parameters, arguments):
                    if output:
                        evaluated.append(None)
                        destinations.append((parameter, argument.split()[-1]))
                    else:
                        evaluated.append(self.evaluate(re.sub(r"^(?:in|ref)\s+", "", argument), values))
                _, results = self.run(name, evaluated)
                for parameter, target in destinations:
                    self.assign(target, results[parameter], values)
                continue
            statement = re.sub(r"^(?:float4|float|int4|var|SpatialInterpolationData)\s+", "", statement)
            match = re.fullmatch(r"([\w.]+)\s*([+\-*/]?=)\s*(.+)", statement, re.S)
            if match is None: raise AssertionError(f"Unsupported sampling statement: {statement}")
            target, operation, rhs = match.groups()
            assignments = re.split(r"(?<![<>!=])=(?!=)", rhs)
            if operation == "=" and len(assignments) > 1:
                targets = [target] + [item.strip() for item in assignments[:-1]]
                result = self.evaluate(assignments[-1], values)
                for item in targets: self.assign(item, result, values)
            else:
                result = self.evaluate(rhs, values)
                if operation != "=":
                    result = self.evaluate(target + operation[0] + "nextValue", dict(values, nextValue=result))
                self.assign(target, result, values)
        return None

    def run(self, name, arguments):
        parameters, body = self.functions[name]
        if len(arguments) != len(parameters): raise AssertionError(f"Wrong source argument count for {name}")
        values = dict(self.constants, **{parameter[0]: argument for parameter, argument in zip(parameters, arguments)})
        if name == "SampleDisplacementFromXZ": self.spatial_samples += 1
        return self.execute(body, values), values

    def temporal(self, time, parameters):
        if "CalculateTemporalSamplingData" in self.functions:
            _, values = self.run("CalculateTemporalSamplingData", [Float32(time), parameters, None, None, None])
            return values["frame0"], values["frame1"], values["alphaT"]
        # Execute the actual old prefix, including its negative remainder rule.
        body = self.functions["SampleDisplacementXZT"][1]
        values = dict(t=Float32(time), parameters=parameters)
        self.execute(body[:body.index("SampleDisplacementFromXZ(")], values)
        return values["f0"], values["f1"], values["alphaT"]

    def raw(self, fixture, x, z, time):
        return self.displacement("SampleDisplacementXZT", fixture, x, z, time)

    def displacement(self, name, fixture, x, z, time):
        _, values = self.run(name, [Float32(x), Float32(z), Float32(time), fixture.parameters, fixture.data, None, None, None])
        return tuple(values[name] for name in ("dispX", "dispY", "dispZ"))

    def height(self, fixture, x, z, time):
        return self.run("SampleHeightXZT", [Float32(x), Float32(z), Float32(time), fixture.parameters, fixture.data])[0]

    def provider(self, fixture, x, z, time):
        text = regression.source(PROVIDER)
        start = text.index("private struct JobSampleDisplacement")
        selected = re.search(r"FFTBakedData\.(\w+)\(", text[start:])[1]
        return self.displacement(selected, fixture, x, z, time)


class Fixture:
    def __init__(self, field, frames=2, resolution=32, first_lod=5, lod_count=1, period=2.0):
        self.parameters = SimpleNamespace(_period=Float32(period), _frameCount=frames,
                                          _textureResolution=resolution, _firstLod=first_lod, _lodCount=lod_count)
        self.data = []
        for frame in range(frames):
            for lod in range(first_lod, first_lod + lod_count):
                width = 2.0 ** (lod - 1)
                for v in range(resolution):
                    for u in range(resolution):
                        values = field((u + .5) / resolution * width, (v + .5) / resolution * width, frame, lod)
                        self.data.extend(half(value) for value in (*values, 0.0))

    def spatial_reference(self, x, z, frame):
        p, result = self.parameters, [0.0] * 3
        for lod in range(p._lodCount):
            width = 2.0 ** (p._firstLod + lod - 1)
            u, v = x / width * p._textureResolution - .5, z / width * p._textureResolution - .5
            i, j = math.floor(u), math.floor(v)
            weights = ((0, 0, (1 - u + i) * (1 - v + j)), (1, 0, (u - i) * (1 - v + j)),
                       (0, 1, (1 - u + i) * (v - j)), (1, 1, (u - i) * (v - j)))
            for di, dj, weight in weights:
                offset = (((frame * p._lodCount + lod) * p._textureResolution + (j + dj) % p._textureResolution)
                          * p._textureResolution + (i + di) % p._textureResolution) * 4
                for channel in range(3): result[channel] += weight * float(self.data[offset + channel])
        return result

    def raw_reference(self, x, z, time):
        p = self.parameters
        frame = (time / float(p._period) % 1.0) * p._frameCount
        index, weight = math.floor(frame), frame % 1.0
        a, b = self.spatial_reference(x, z, index), self.spatial_reference(x, z, (index + 1) % p._frameCount)
        return tuple((1 - weight) * first + weight * second for first, second in zip(a, b))


def wave(x, z, frame, lod):
    angle = math.tau * x / (2.0 ** (lod - 1))
    return -math.sin(angle), math.cos(angle), 0.0


class FFTQueries(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.sampler = SourceSampler()
        cls.wave = Fixture(wave, resolution=128)
        cls.opposites = Fixture(lambda x, z, frame, lod: tuple((1 if frame == 0 else -1) * v for v in wave(x, z, frame, lod)), resolution=128)

    def assertVectorClose(self, actual, expected, tolerance=1e-5):
        for a, b in zip(actual, expected): self.assertAlmostEqual(a, b, delta=tolerance)

    def test_temporal_indices_and_weights_stay_in_range(self):
        for count in (1, 2, 7, 128, 600):
            for period in (2.0, 17.25, 32.0):
                p = SimpleNamespace(_period=Float32(period), _frameCount=count)
                for time in (-2 * period, -period, -.06, -1e-8, -1e-30, 0, 1e-8, period - 1e-5, period, 2 * period):
                    first, second, weight = self.sampler.temporal(time, p)
                    self.assertTrue(0 <= first < count and 0 <= second < count, (time, first, second))
                    self.assertTrue(0 <= weight < 1 and math.isfinite(weight), (time, weight))

    def test_negative_whole_periods_sample_frame_zero(self):
        fixture = Fixture(lambda x, z, frame, lod: (frame * .125, frame * .25, -.1 * frame), frames=7, period=32)
        reference = self.sampler.raw(fixture, .75, -1.25, 0)
        for time in (-96, -64, -32, 0, 32, 64):
            self.assertVectorClose(self.sampler.raw(fixture, .75, -1.25, time), reference)

    def test_tiny_negative_time_wraps_without_large_alpha(self):
        fixture = Fixture(lambda x, z, frame, lod: (frame, frame * .5, 0), frames=7, period=32)
        for time in (-1e-8, -1e-30):
            self.assertVectorClose(self.sampler.raw(fixture, .5, .5, time), (0, 0, 0), tolerance=1e-6)

    def test_velocity_history_across_time_zero_is_finite(self):
        step = float(re.search(r"s_finiteDiffDt\s*=\s*([0-9.]+)f", regression.source(PROVIDER))[1])
        for time in (0, .03, .06, .06 - 1e-8):
            previous = Float32(time) - Float32(step)
            velocity = (self.sampler.height(self.opposites, 4, 3, time)
                        - self.sampler.height(self.opposites, 4, 3, previous)) / Float32(step)
            self.assertTrue(math.isfinite(velocity))
            self.assertLess(abs(velocity), 10)

    def test_raw_api_preserves_periodic_bilinear_half_field(self):
        fixture = Fixture(lambda x, z, frame, lod: (.1 * math.sin(x), .2 * math.cos(z + frame), .05 * math.sin(x + z)),
                          frames=3, resolution=16, lod_count=2)
        # Four independent lane inputs, including negative positions and seams.
        for x, z, time in ((.37, 1.21, .17), (-.25, -.125, .91), (16, 32, 1.5), (-32, 16, 0)):
            self.assertVectorClose(self.sampler.raw(fixture, x, z, time), fixture.raw_reference(x, z, time))

    def test_first_texel_center_predecessor_wraps_spatial_indices(self):
        fixture = Fixture(lambda x, z, frame, lod: (.1 * math.sin(x), .2 * math.cos(z), .05 * (frame + lod)),
                          frames=2, resolution=64, first_lod=4, lod_count=2)
        # The previous representable binary32 value below the first texel centre.
        # Wrapping its tiny negative texel offset can round back to resolution.
        center = .0625
        bits = struct.unpack("I", struct.pack("f", center))[0]
        previous = struct.unpack("f", struct.pack("I", bits - 1))[0]
        for x, z in ((previous, 0), (0, previous), (previous, previous), (2 * previous, 0), (0, 2 * previous)):
            for time in (0, 1):
                self.assertVectorClose(self.sampler.raw(fixture, x, z, time), fixture.raw_reference(x, z, time))

    def test_provider_displacement_height_obeys_world_query_contract(self):
        for x, z in ((4, 3), (3, 2), (-4, -1), (20, 19)):
            displacement = self.sampler.provider(self.wave, x, z, .37)
            height = self.sampler.height(self.wave, x, z, .37)
            self.assertEqual(displacement[1], height)

    def test_opposite_frames_cancel_before_height_inversion(self):
        self.assertVectorClose(self.sampler.raw(self.opposites, 4, 3, .5), (0, 0, 0), tolerance=1e-7)
        self.assertAlmostEqual(self.sampler.height(self.opposites, 4, 3, .5), 0, delta=1e-7)
        self.assertVectorClose(self.sampler.provider(self.opposites, 4, 3, .5), (0, 0, 0), tolerance=1e-7)

    def test_interpolated_affine_field_matches_independent_matrix_inverse(self):
        def field(x, z, frame, lod):
            sign = 1 if frame == 0 else -1
            return sign * (.1 * x + .05 * z), .2 * x - .1 * z + frame * .25, sign * (.04 * x - .08 * z)
        fixture = Fixture(field, resolution=64)
        for weight in (.2, .4, .7):
            scale = 1 - 2 * weight
            a, b, c, d = 1 + .1 * scale, .05 * scale, .04 * scale, 1 - .08 * scale
            determinant = a * d - b * c
            for x, z in ((3, 4), (5, 3)):
                u, v = (d * x - b * z) / determinant, (a * z - c * x) / determinant
                expected = x - u, .2 * u - .1 * v + weight * .25, z - v
                self.assertVectorClose(self.sampler.provider(fixture, x, z, weight), expected, tolerance=.001)
                self.assertAlmostEqual(self.sampler.height(fixture, x, z, weight), expected[1], delta=.001)

    def test_choppy_wave_inverse_has_bounded_residual(self):
        p = self.wave.parameters
        width = 2.0 ** (p._firstLod - 1)
        row = [float(self.wave.data[index * 4]) for index in range(p._textureResolution)]
        slope_bound = max(abs(b - a) for a, b in zip(row, row[1:] + row[:1])) / (width / p._textureResolution)
        self.assertLess(slope_bound, 1)
        for x in (3, 4, 5):
            displacement = self.sampler.provider(self.wave, x, 3, .37)
            u = x - displacement[0]
            sampled = self.wave.raw_reference(u, 3 - displacement[2], .37)
            initial_step = abs(self.wave.raw_reference(x, 3, .37)[0])
            # Contraction bound on the residual after N+1 evaluations, independent
            # of the implementation's recurrence and rounded texture samples.
            bound = initial_step * slope_bound ** (self.sampler.constants["kIterationCount"] + 1)
            self.assertLessEqual(abs(sampled[0] - displacement[0]), bound + 1e-5)

    def test_raw_sampler_does_not_gain_displacement_inversion(self):
        raw = self.sampler.raw(self.wave, 4, 3, .37)
        self.assertAlmostEqual(raw[1], 0, delta=1e-7)
        self.assertLess(self.sampler.height(self.wave, 4, 3, .37), -.35)

    def test_height_keeps_existing_spatial_lookup_budget(self):
        self.sampler.spatial_samples = 0
        self.sampler.height(self.wave, 4, 3, .37)
        self.assertEqual(self.sampler.spatial_samples, 8)
        self.sampler.spatial_samples = 0
        self.sampler.raw(self.wave, 4, 3, .37)
        self.assertEqual(self.sampler.spatial_samples, 2)

    def test_editor_namespace_is_excluded_from_player_source(self):
        text = regression.source(DATA)
        conditional = re.search(r"#if UNITY_EDITOR\s+using UnityEditor;\s+#endif", text)
        self.assertIsNotNone(conditional)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read an earlier Git revision without checking it out")
    arguments, remaining = parser.parse_known_args()
    regression.SOURCE_REF = arguments.ref
    print("FFT queries: source equations, binary32 arithmetic and periodic half fixtures; no Unity/Burst/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
