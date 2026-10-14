#!/usr/bin/env python3
"""Issue 8: execute production rounding helpers with binary32/uint32 storage.

Offline only: no Unity binding, GPU filtering, exp/FMA accuracy or performance
claim. Test both RTZ and RNE conversion/store models; use real shader bodies.
The existing simulation saturate remains after integration, while additive draws
can still exceed one. No shared tests are modified.
"""
import math
from pathlib import Path
import re
import statistics
import struct
from types import SimpleNamespace
import unittest

from check_foam_dynamics import execute as execute_scalar_source

ROOT = Path(__file__).resolve().parents[1]
CREST = ROOT / "crest/Assets/Crest/Crest"
SHADER = (CREST / "Shaders/Resources/UpdateFoam.compute").read_text(encoding="utf-8-sig")
MANAGER = (CREST / "Scripts/LodData/LodDataMgrFoam.cs").read_text(encoding="utf-8-sig")
PERSISTENT = (CREST / "Scripts/LodData/LodDataMgrPersistent.cs").read_text(encoding="utf-8-sig")


def f32(value):
    return struct.unpack("<f", struct.pack("<f", value))[0]


def decode(bits):
    return struct.unpack("<e", struct.pack("<H", bits))[0]


def encode(value, mode="RNE"):
    bits = struct.unpack("<H", struct.pack("<e", value))[0]
    return bits - (mode == "RTZ" and decode(bits) > value)


def body(text, name):
    text = re.sub(r"//[^\n]*|/\*.*?\*/", "", text, flags=re.S)
    match = re.search(r"(?m)^\s*(?:(?:static|internal|public|private|protected|override)\s+)*(?:float|void)\s+"
                      + name + r"\s*\([^{}]*\)\s*\{", text)
    if not match:
        raise AssertionError(f"Missing function {name}")
    start, depth = match.end(), 1
    for end in range(start, len(text)):
        depth += (text[end] == "{") - (text[end] == "}")
        if not depth:
            return text[start:end]
    raise AssertionError("Unclosed function")


def translate(name, arguments):
    """Translate just declarations, assignments, if and return in these helpers.

    Unknown statements fail. Declared floats round at assignment; uints wrap.
    This does not model every intermediate shader instruction.
    """
    lines, indent, kinds = [f"def {name}({arguments}):"], 1, {}
    for token in re.split(r"([{};])", body(SHADER, name)):
        token = token.strip()
        if not token or token == ";":
            continue
        if token == "{":
            indent += 1
            continue
        if token == "}":
            indent -= 1
            continue
        token = re.sub(r"(0x[\da-fA-F]+|\d+)u\b", r"\1", token)
        token = token.replace("||", " or ").replace("&&", " and ")
        token = re.sub(r"!(?!=)", "not ", token)
        token = token.replace("float(", "f32(")
        token = token.replace("CrestFoamRoundingNoise(id)", "CrestFoamRoundingNoise(id, _CrestFoamRoundingStep)")
        conditional = re.fullmatch(r"if\s*\((.*)\)", token, re.S)
        if conditional:
            statement = f"if {conditional[1]}:"
        elif token.startswith("return "):
            expression = token[7:]
            ternary = re.fullmatch(r"(.*?)\?\s*(.*?)\s*:\s*(.*)", expression, re.S)
            if ternary:
                expression = f"({ternary[2]} if {ternary[1]} else {ternary[3]})"
            statement = "return " + expression
        else:
            assignment = re.fullmatch(r"(?:(?:const\s+)?(uint|float)\s+)?(\w+)\s*=\s*(.*)", token, re.S)
            if not assignment:
                raise AssertionError(f"Unsupported statement: {token}")
            kind, variable, expression = assignment.groups()
            if kind:
                kinds[variable] = kind
            expression = f"({expression}) & 0xffffffff" if kinds[variable] == "uint" else f"f32({expression})"
            statement = f"{variable} = {expression}"
        lines.append("    " * indent + statement)
    return "\n".join(lines)


def helpers(mode):
    namespace = {"f32": f32, "f16tof32": decode, "f32tof16": lambda value: encode(value, mode)}
    exec(translate("CrestFoamRoundingNoise", "id, _CrestFoamRoundingStep"), namespace)
    exec(translate("CrestFoamRoundToHalf", "foam, id, _CrestFoamStochasticRounding, _SimDeltaTime, _CrestFoamRoundingStep"), namespace)
    return namespace["CrestFoamRoundToHalf"], namespace["CrestFoamRoundingNoise"]


ROUND, NOISE = helpers("RNE")
IDS = [SimpleNamespace(x=index % 32, y=index // 32, z=index % 3) for index in range(1024)]


def decay(frequency, rate, duration=2, count=1024, rounding=True, points=None):
    dt = f32(1 / frequency)
    factor = f32(math.exp(-f32(f32(rate) * dt)))
    values = [1.0] * count
    points = IDS if points is None else points
    for step in range(1, round(frequency * duration) + 1):
        for index in range(count):
            value = f32(values[index] * factor)
            value = ROUND(value, points[index], True, dt, step) if rounding else value
            values[index] = decode(encode(value, "RTZ"))
    return values


class FoamIssue8Tests(unittest.TestCase):
    def test_all_finite_positive_half_values_are_fixed_points(self):
        for mode in ("RTZ", "RNE"):
            rounding, _ = helpers(mode)
            for bits in range(0x7c00):
                value = decode(bits)
                self.assertEqual(rounding(value, IDS[0], True, .005, 19), value)

    def test_adjacent_values_subnormals_and_above_one(self):
        for bits in (0, 1, 511, 1023, 1024, 0x2800, 0x3800, 0x3bff, 0x3c00, 0x4000, 0x7bfe):
            lower, upper = decode(bits), decode(bits + 1)
            for fraction in (.125, .5, .875):
                value = f32(lower + fraction * (upper - lower))
                outputs = []
                for mode in ("RTZ", "RNE"):
                    rounding, _ = helpers(mode)
                    outputs.append([rounding(value, point, True, .005, 27) for point in IDS])
                self.assertEqual(outputs[0], outputs[1])
                self.assertEqual(set(outputs[0]), {lower, upper})
                self.assertAlmostEqual(statistics.mean(outputs[0]), value, delta=.04 * (upper - lower))
                for output in outputs[0]:
                    self.assertEqual(decode(encode(output, "RTZ")), output)
                    self.assertEqual(decode(encode(output, "RNE")), output)
        self.assertEqual(decode(1), 2 ** -24)

    def test_zero_time_and_float32_bypass_are_exact(self):
        for value in (0, 2 ** -25, 2 ** -24, .123456, 1, 1.234567, 65504):
            value = f32(value)
            for step in (0, 1, 0x7fffffff, 0x80000000, 0xffffffff):
                self.assertEqual(ROUND(value, IDS[0], True, 0, step), value)
                self.assertEqual(ROUND(value, IDS[0], False, .005, step), value)

    def test_noise_changes_with_substep_pixel_and_slice(self):
        self.assertEqual(len({NOISE(IDS[0], step) for step in range(1, 401)}), 400)
        self.assertGreater(len({NOISE(point, 13) for point in IDS}), 1000)
        self.assertEqual(len({NOISE(SimpleNamespace(x=3, y=8, z=z), 13) for z in range(15)}), 15)
        self.assertTrue(all(0 <= NOISE(IDS[0], step) < 1 for step in (0, 0x7fffffff, 0x80000000, 0xffffffff)))

    def test_real_dispatch_seed_and_format_bindings(self):
        params = body(MANAGER, "SetAdditionalSimParams")
        self.assertRegex(params, r"if\s*\(_inputTimeScale > 0f\)\s*\{\s*_foamRoundingStep = unchecked\(_foamRoundingStep \+ 1\);")
        self.assertIn("SetInt(sp_CrestFoamRoundingStep, _foamRoundingStep)", params)
        self.assertIn("var format = CompatibleTextureFormat", params)
        for format_name in ("R16_SFloat", "R16G16_SFloat", "R16G16B16A16_SFloat"):
            self.assertIn("format == GraphicsFormat." + format_name, params)
        self.assertIn("CalculateFoamInputScale(substepDt, Settings._foamFadeRate)", body(MANAGER, "GetSimSubstepData"))
        persistent = body(PERSISTENT, "BuildCommandBuffer")
        loop = persistent[persistent.index("for (int stepi"):]
        self.assertLess(loop.index("SetFloat(sp_SimDeltaTime, substepDt)"), loop.index("SetAdditionalSimParams(_renderSimProperties)"))
        self.assertLess(loop.index("SetAdditionalSimParams(_renderSimProperties)"), loop.index("buf.DispatchCompute"))
        self.assertIn("substepDt = 0f", persistent)
        store = re.search(r"_LD_TexArray_Target\[id\]\s*=\s*([^;]+);", SHADER)[1]
        self.assertEqual(store, "CrestFoamRoundToHalf(saturate(foam), id)")
        # Values above one are still integrated BEFORE the existing clamp.
        self.assertLess(SHADER.index("foam *= decayAndSource.x"), SHADER.index(store))

    def test_seed_sequence_tracks_multiple_substeps_and_zero_time(self):
        def input_scale(dt, rate):
            def integrated(delta_time, fade_rate):
                return execute_scalar_source(body(MANAGER, "IntegratedSourceWeight"),
                                             {"deltaTime": delta_time, "fadeRate": fade_rate})[1]
            return execute_scalar_source(body(MANAGER, "CalculateFoamInputScale"),
                                         {"deltaTime": dt, "fadeRate": rate,
                                          "IntegratedSourceWeight": integrated})[1]

        params = body(MANAGER, "SetAdditionalSimParams")
        predicate = re.search(r"if\s*\(([^)]+)\)", params)[1].replace("0f", "0")
        increment = re.search(r"_foamRoundingStep = unchecked\(([^)]+)\)", params)[1]
        # Each batch computes one input scale. The persistent loop then invokes
        # SetAdditionalSimParams before every dispatch, including its zero-dt copy.
        for rate in (0, .02, .8, 20):
            step, sequence = 0, []
            for count, dt in ((3, 1 / 200), (0, 0), (2, 1 / 200), (0, 0), (1, 1 / 30)):
                scale = input_scale(dt, rate)
                self.assertEqual(scale > 0, dt > 0)
                for _ in range(max(count, 1)):
                    if eval(predicate, {"__builtins__": {}}, {"_inputTimeScale": scale}):
                        step = eval(increment, {"__builtins__": {}}, {"_foamRoundingStep": step}) & 0xffffffff
                    sequence.append(step)
            self.assertEqual(sequence, [1, 2, 3, 3, 4, 5, 5, 6])
        self.assertEqual(eval(increment, {"__builtins__": {}}, {"_foamRoundingStep": 0x7fffffff}) & 0xffffffff, 0x80000000)
        self.assertEqual(eval(increment, {"__builtins__": {}}, {"_foamRoundingStep": 0xffffffff}) & 0xffffffff, 0)

    def test_direct_gpu_fixtures_bind_rounding_and_measure_spatial_mean(self):
        for path, function in (("RealismAudit.cs", "AuditFoamGpuDecay"), ("PlayerRetest.cs", "RunFoamStorage")):
            fixture = body((ROOT / "tools/UnityQualityTests" / path).read_text(encoding="utf-8-sig"), function)
            self.assertIn("var actualFormat = targets[0].graphicsFormat", fixture)
            for name in ("R16_SFloat", "R16G16_SFloat", "R16G16B16A16_SFloat"):
                self.assertIn("actualFormat == UnityEngine.Experimental.Rendering.GraphicsFormat." + name, fixture)
            loop = fixture[fixture.index("for (var step ="):fixture.index("Graphics.ExecuteCommandBuffer")]
            self.assertIn('SetComputeIntParam(foam, "_CrestFoamStochasticRounding", roundToHalf ? 1 : 0)', loop)
            self.assertIn('SetComputeIntParam(foam, "_CrestFoamRoundingStep", step + 1)', loop)
            self.assertLess(loop.index('"_CrestFoamRoundingStep"'), loop.index("DispatchCompute"))
            self.assertIn("const int border = 2", fixture)
            self.assertIn("sum += value", fixture)
            self.assertIn("sum / sampleCount", fixture)
            self.assertIn("6f * Mathf.Sqrt(2f * frequency / sampleCount) / 2048f", fixture)
            self.assertIn("Corrected foam decay spatial mean", fixture)

    def test_gpu_audit_region_meets_variance_budget_and_rejects_old_bias(self):
        points = [SimpleNamespace(x=x, y=y, z=0) for y in range(2, 30) for x in range(2, 30)]
        values = decay(200, .8, count=len(points), points=points)
        tolerance = .001 + 6 * math.sqrt(400 / len(points)) / 2048
        error = statistics.mean(values) / math.exp(-1.6) - 1
        print(f"GPU audit CPU region: {len(points)} texels, mean error={100 * error:+.5f}%, tolerance={100 * tolerance:.5f}%", flush=True)
        self.assertLess(abs(error), tolerance)
        self.assertGreater(abs(.17626953125 / math.exp(-1.6) - 1), tolerance)

    def test_lifetime_mean_and_slow_fade_do_not_stall(self):
        for frequency, rate, duration, count in ((15, .8, 2, 1024), (30, .8, 2, 1024), (60, .8, 2, 1024), (200, .8, 2, 1024), (200, .02, 2, 1024), (200, .05, 20, 256)):
            values = decay(frequency, rate, duration, count)
            expected = math.exp(-rate * duration)
            error = statistics.mean(values) / expected - 1
            print(f"{frequency} Hz, {rate}/s, {duration}s: mean={statistics.mean(values):.10f}, error={100 * error:+.5f}%, texels={count}", flush=True)
            self.assertLess(abs(error), .002)
            self.assertTrue(all(0 < value <= 1 for value in values))
        old = decay(200, .8, count=1, rounding=False)[0]
        self.assertAlmostEqual(old, .17626953125)
        self.assertLess(old / math.exp(-1.6) - 1, -.12)


if __name__ == "__main__":
    print("Production helper CPU models; no Unity/GPU execution.", flush=True)
    unittest.main()
