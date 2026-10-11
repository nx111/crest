#!/usr/bin/env python3
"""Wave-spectrum and phase invariants evaluated from the actual source equations.

These CPU checks cover dispersion, spectral measure, loop timing, IEEE phase
precision and readback source contracts. They do not execute Unity, FFT kernels,
collision baking or a GPU.
Use --ref <commit> to inspect an earlier source revision without checking it out.
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

FFT = regression.CREST + "Shaders/Resources/FFT/FFTSpectrum.compute"
FFT_CPU = regression.CREST + "Scripts/Shapes/FFT/FFTCompute.cs"
BAKED = regression.CREST + "Scripts/Shapes/FFT/FFTBakedData.cs"
BAKER = regression.CREST + "Scripts/Shapes/FFT/FFTBaker.cs"
GERSTNER = regression.CREST + "Shaders/Resources/Gerstner.compute"
LEGACY = regression.CREST + "Shaders/OceanInputs/GerstnerShared.hlsl"


@lru_cache(maxsize=None)
def source(path):
    return regression.source(path)


@lru_cache(maxsize=None)
def function(path, name):
    declaration = re.search(r"(?m)^\s*(?:(?:public|private|protected|internal|static)\s+)*"
                            r"(?:void|float[234]?|half[234]?|FFTBakedData)\s+" + name + r"\s*\([^{}]*\)\s*\{", source(path))
    if declaration is None: raise AssertionError(f"Missing function: {name}")
    return regression.body(source(path)[declaration.start():], name)


FUNCTIONS = {"sqrt": math.sqrt, "abs": abs, "ceil": math.ceil, "exp": math.exp, "pow": pow,
             "sin": math.sin, "cos": math.cos, "max": max}


@lru_cache(maxsize=None)
def expression(text):
    text = re.sub(r"(?<=\d)[fF]\b", "", text.strip()).replace("Mathf.Abs", "abs")
    text = text.replace("&&", " and ").replace("||", " or ")
    if "?" in text:
        condition, choices = text.split("?", 1)
        positive, negative = choices.split(":", 1)
        text = f"({positive} if {condition} else {negative})"
    tree = ast.parse(text.strip(), mode="eval")
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Call, ast.Name, ast.Load, ast.Constant,
               ast.Add, ast.Sub, ast.Mult, ast.Div, ast.Mod, ast.USub, ast.Attribute, ast.Subscript,
               ast.Compare, ast.Lt, ast.LtE, ast.Gt, ast.GtE, ast.Eq, ast.IfExp, ast.BoolOp, ast.And, ast.Or)
    attributes = {"x", "y", "_waveDirX", "_waveDirZ", "_twoPiOverWavelength", "_phase", "_phase2",
                  "_omega", "_gravityScale", "_period"}
    for node in ast.walk(tree):
        if not isinstance(node, allowed): raise AssertionError(f"Unsupported expression: {text}")
        if isinstance(node, ast.Call) and (not isinstance(node.func, ast.Name) or node.func.id not in FUNCTIONS):
            raise AssertionError(f"Unsupported call: {text}")
        if isinstance(node, ast.Attribute) and node.attr not in attributes:
            raise AssertionError(f"Unsupported field: {text}")
    return compile(tree, "<wave-source-expression>", "eval")


def evaluate(text, values):
    try:
        return eval(expression(text), {"__builtins__": {}}, {**FUNCTIONS, **values})
    except (ArithmeticError, ValueError) as error:
        raise AssertionError(f"Invalid wave arithmetic in {text}: {error}") from error


def balanced_end(text, start, opening, closing):
    depth = 1
    for index in range(start + 1, len(text)):
        depth += (text[index] == opening) - (text[index] == closing)
        if depth == 0: return index
    raise AssertionError("Unbalanced source block")


def execute(body, values):
    """Small scalar interpreter for current and baseline dispersion helpers.

    Supports assignments, return, and braced if blocks. Unknown control flow is
    rejected rather than silently approximated. The baseline loop quantization
    therefore executes as well, instead of being removed by a source assertion.
    """
    body = body.strip()
    while body:
        if re.match(r"if\s*\(", body):
            start = body.index("(")
            end = balanced_end(body, start, "(", ")")
            condition = body[start + 1:end]
            block_start = end + 1
            while body[block_start].isspace(): block_start += 1
            if body[block_start] != "{": raise AssertionError("Expected a braced scalar conditional")
            block_end = balanced_end(body, block_start, "{", "}")
            if evaluate(condition, values): execute(body[block_start + 1:block_end], values)
            body = body[block_end + 1:].strip()
            continue
        statement, body = body.split(";", 1)
        statement, body = statement.strip(), body.strip()
        if statement.startswith("return "):
            values["_return"] = evaluate(statement[7:], values)
            return values
        match = re.fullmatch(r"(?:(?:float|half|var)\s+)?(\w+)\s*=\s*(.+)", statement, re.S)
        if match is None: raise AssertionError(f"Unsupported scalar statement: {statement}")
        values[match[1]] = evaluate(match[2], values)
    return values


def fft_values(period, gravity=9.81, wind=10):
    pi2 = float(re.search(r"#define\s+PI2\s+([0-9.]+)", source(FFT))[1])
    return {"PI2": pi2, "_Period": period, "_Gravity": gravity, "_WindSpeed": wind}


def dispersion(k, period=-1, gravity=9.81):
    result = execute(function(FFT, "DeepDispersion"), {**fft_values(period, gravity), "k": k})
    return result["w"], result["dwdk"]


def evolving_frequency(k, period, gravity=9.81):
    frequency, _ = dispersion(k, period, gravity)
    update = function(FFT, "SpectrumUpdate")
    if re.search(r"\bw\s*=\s*CrestQuantizeWaveFrequency\(w\)", update):
        frequency = execute(function(FFT, "CrestQuantizeWaveFrequency"),
                            {**fft_values(period, gravity), "frequency": frequency})["_return"]
    return frequency


def spectral_measure(k, period, gravity=9.81, wind=10):
    frequency, derivative = dispersion(k, period, gravity)
    values = {**fft_values(period, gravity, wind), "w": frequency, "dwdk": derivative, "kMag": k, "dK": .37}
    wind_factor = execute(function(FFT, "PiersonMoskowitzWindTerm"), dict(values))["_return"]
    jacobian = re.search(r"deltaSPos\s*\*=\s*([^;]*\bdwdk\b[^;]*);", function(FFT, "SpectrumInitalize"))
    if jacobian is None: raise AssertionError("Missing frequency-to-wave-vector measure")
    return wind_factor * evaluate(jacobian[1], values)


def time_bindings(time, scale, period):
    body = function(FFT_CPU, "UpdateSpectrum")
    values = {"time": time, "_spectrum": SimpleNamespace(_gravityScale=scale), "_loopPeriod": period}
    for name, equation in re.findall(r"\bvar\s+(\w+)\s*=\s*([^;]+);", body):
        values[name] = evaluate(equation, values)
    bindings = {arguments[1].split(".")[-1]: evaluate(arguments[2], values)
                for arguments in calls(body, "buf.SetComputeFloatParam")
                if arguments[1].split(".")[-1] in ("s_Time", "s_Period")}
    return bindings["s_Time"], bindings["s_Period"]


def rounded(value, kind):
    try:
        return struct.unpack("e" if kind == "half" else "f", struct.pack("e" if kind == "half" else "f", value))[0]
    except OverflowError:
        return math.copysign(math.inf, value)


def phase_inputs(n, m, domain):
    # The data buffer stores float32 wave-vector magnitude and unit direction.
    kx, kz = (rounded(math.tau * index / domain, "float") for index in (n, m))
    k = rounded(math.hypot(kx, kz), "float")
    return {"k": k, "dx": rounded(kx / k, "float"), "dz": rounded(kz / k, "float"),
            "phase": rounded(.7, "float"), "phase2": rounded(1.1, "float"),
            "omega": rounded(math.sqrt(9.81 * k), "float")}


def phase_at(path, inputs, x, z, time=0, reverse=False):
    """Execute one lane of actual phase assignments, rounding at declarations.

    This is an IEEE storage diagnostic, not an emulation of one GPU's promotion,
    instruction fusion or trigonometric implementation.
    """
    body = function(path, "ComputeGerstner")
    values = {"worldPosXZ": SimpleNamespace(x=rounded(x, "float"), y=rounded(z, "float")),
              "_CrestTime": rounded(time, "float"), "vi": 0}
    if path == GERSTNER:
        values["data"] = SimpleNamespace(_waveDirX=inputs["dx"], _waveDirZ=inputs["dz"],
                                         _twoPiOverWavelength=inputs["k"], _phase=inputs["phase"],
                                         _phase2=inputs["phase2"], _omega=inputs["omega"])
        names = ("Dx", "Dz", "k", "kx", "kz", "x")
    else:
        for uniform, value in (("_WaveDirX", inputs["dx"]), ("_WaveDirZ", inputs["dz"]),
                               ("_TwoPiOverWavelengths", inputs["k"]), ("_Phases", inputs["phase"])):
            kind = re.search(r"\b(half|float)4\s+" + uniform + r"\[", source(path))[1]
            values[uniform] = [rounded(value, kind)]
        names = ("Dx", "Dz", "k", "x")
    for name in names:
        declaration = re.search(r"\b(half|float)4\s+" + name + r"\s*=\s*([^;]+);", body)
        if declaration is None: raise AssertionError(f"Missing phase assignment {name}")
        values[name] = rounded(evaluate(declaration[2], values), declaration[1])
    angles = re.findall(r"\b(half|float)4\s+angle\s*=\s*([^;]+);", body)
    kind, equation = angles[1 if reverse and path == GERSTNER else 0]
    return rounded(evaluate(equation, values), kind)


class WaveSpectrum(unittest.TestCase):
    def test_dispersion_and_jacobian_follow_independent_wave_equation(self):
        for gravity in (.4, 9.81, 24):
            for k in (.002, .05, .7, 5, 75):
                for period in (-1, 0, 4, 32, 128):
                    actual_frequency, actual_derivative = dispersion(k, period, gravity)
                    expected = math.sqrt(gravity * k)
                    self.assertAlmostEqual(actual_frequency, expected, delta=expected * 1e-12)
                    step = k * 1e-5
                    numerical_derivative = (math.sqrt(gravity * (k + step)) - math.sqrt(gravity * (k - step))) / (2 * step)
                    self.assertAlmostEqual(actual_derivative, numerical_derivative, delta=numerical_derivative * 1e-9)

    def test_spectral_measure_is_independent_of_animation_loop_period(self):
        for wavelength in (.25, 1, 8, 32, 64, 128, 256):
            k = math.tau / wavelength
            for wind in (5, 10, 15, 30):
                reference = spectral_measure(k, -1, wind=wind)
                for period in (4, 8, 32, 128):
                    self.assertAlmostEqual(spectral_measure(k, period, wind=wind), reference,
                                           delta=max(reference * 1e-12, 1e-300))
        self.assertNotIn("CrestQuantizeWaveFrequency", function(FFT, "SpectrumInitalize"))

    def test_evolution_is_periodic_and_frequency_error_is_bounded(self):
        for period in (4, 7.25, 32, 128):
            for k in (.002, .05, .7, 5, 75):
                actual = evolving_frequency(k, period)
                natural = math.sqrt(9.81 * k)
                cycles = actual * period / math.tau
                self.assertAlmostEqual(cycles, round(cycles), delta=max(1, cycles) * 1e-10)
                self.assertGreaterEqual(actual + 1e-10, natural)
                self.assertLess(actual - natural, math.tau / period + 1e-10)
        for period in (-1, 0): self.assertEqual(evolving_frequency(.7, period), math.sqrt(9.81 * .7))

    def test_fractional_and_reverse_time_scales_match_cpu_bake_period(self):
        # The bake and both query entry points use unscaled time / saved period.
        for name in ("SampleHeightXZT", "SampleDisplacementXZT"):
            body = function(BAKED, name)
            self.assertIn("t / parameters._period", body)
        for period in (4, 17, 32, 128):
            for scale in (.125, .5, 1, 1.3, 2.75, 25, -.5, -1.3):
                for k in (.012, .08, .7, 5):
                    time0, shader_period = time_bindings(.37 * period, scale, period)
                    time1, _ = time_bindings(1.37 * period, scale, period)
                    frequency = evolving_frequency(k, shader_period)
                    theta0, theta1 = frequency * time0, frequency * time1
                    self.assertAlmostEqual(math.sin(theta0), math.sin(theta1), delta=2e-7)
                    self.assertAlmostEqual(math.cos(theta0), math.cos(theta1), delta=2e-7)

    def test_zero_time_scale_freezes_with_finite_phase(self):
        for period in (-1, 0, 4, 32):
            for time in (0, .2, 1000):
                shader_time, shader_period = time_bindings(time, 0, period)
                for k in (0, .002, 2):
                    phase = evolving_frequency(k, shader_period) * shader_time
                    self.assertTrue(math.isfinite(phase))
                    self.assertEqual(phase, 0)

    def test_zero_mode_and_calm_inputs_do_not_evaluate_singular_spectral_terms(self):
        self.assertEqual(dispersion(0), (0, 0))
        body = function(FFT, "SpectrumInitalize")
        start = body.index("if")
        condition_start = body.index("(", start)
        condition_end = balanced_end(body, condition_start, "(", ")")
        condition = body[condition_start + 1:condition_end]
        for coord, gravity, wind in (((0, 0), 9.81, 10), ((4, 1), 0, 10), ((4, 1), 9.81, 0)):
            self.assertTrue(evaluate(condition, {"coord": SimpleNamespace(x=coord[0], y=coord[1]),
                                                 "_Gravity": gravity, "_WindSpeed": wind}))
        self.assertLess(body.index("return;", condition_end), body.index("DeepDispersion"))

    def test_gerstner_phase_retains_subframe_motion_after_one_hour(self):
        inputs = phase_inputs(1, 0, 1)
        for reverse in (False, True):
            sign = 1 if reverse else -1
            offset = inputs["phase2" if reverse else "phase"]
            for start in (60, 600, 3600):
                times = [start + index / 60 for index in range(60)]
                actual = [phase_at(GERSTNER, inputs, 0, 0, time, reverse) for time in times]
                self.assertEqual(len(set(actual)), len(times))
                for time, angle in zip(times, actual):
                    reference = offset + sign * inputs["omega"] * rounded(time, "float")
                    self.assertAlmostEqual(math.cos(angle), math.cos(reference), delta=.002)

    def test_gerstner_phase_stays_finite_past_half_range(self):
        inputs = phase_inputs(1, 0, 1)
        for time in (10000, 14400):
            for reverse in (False, True):
                self.assertTrue(math.isfinite(phase_at(GERSTNER, inputs, 0, 0, time, reverse)))

    def test_integer_wave_vectors_keep_periodic_spatial_phase(self):
        for path in (GERSTNER, LEGACY):
            for domain in (.5, 4, 128, 8192):
                for n, m in ((1, 2), (3, 2), (7, 5), (12, -3)):
                    inputs = phase_inputs(n, m, domain)
                    x, z = domain * .13, domain * .31
                    origin = phase_at(path, inputs, x, z)
                    along_x = phase_at(path, inputs, x + domain, z) - origin
                    along_z = phase_at(path, inputs, x, z + domain) - origin
                    self.assertAlmostEqual(along_x, math.tau * n, delta=3e-5)
                    self.assertAlmostEqual(along_z, math.tau * m, delta=3e-5)

    def test_legacy_phase_is_not_requantized_through_uniform_arrays(self):
        inputs = phase_inputs(3, 2, 4)
        for x, z in ((.13, .31), (1024, 768), (100000, -10000)):
            actual = phase_at(LEGACY, inputs, x, z)
            reference = inputs["k"] * (inputs["dx"] * x + inputs["dz"] * z) + inputs["phase"]
            self.assertTrue(math.isfinite(actual))
            self.assertAlmostEqual(actual, reference, delta=max(.00001, abs(reference) * 2e-7))

    def test_bake_readback_matches_linear_half_storage_contract(self):
        body = function(BAKER, "BakeFFT")
        target, = calls(body, "new RenderTexture")
        staging, = calls(body, "new Texture2D")
        # Unity requires ReadPixels source/destination formats to match. These
        # named formats both map to four 16-bit float channels; ARGBFloat does not.
        formats = {"RenderTextureFormat.ARGBHalf": (4, 16),
                   "RenderTextureFormat.ARGBFloat": (4, 32),
                   "TextureFormat.RGBAHalf": (4, 16)}
        self.assertEqual(formats[target[3]], formats[staging[2]])
        self.assertEqual(formats[staging[2]], (4, 16))
        self.assertEqual(target[4], "RenderTextureReadWrite.Linear")
        self.assertEqual(staging[3:], ["false", "true"])
        readback, = calls(body, "stagingTexture.ReadPixels")
        self.assertEqual(readback[1:], ["0", "0", "false"])
        self.assertIn("stagingTexture.GetRawTextureData<half>().ToArray()", body)

    def test_bake_transient_resources_and_active_target_have_finally_cleanup(self):
        # Structural lifetime check only: this does not simulate Unity failures.
        body = function(BAKER, "BakeFFT")
        capture = re.search(r"var\s+(\w+)\s*=\s*RenderTexture.active\s*;", body)
        self.assertIsNotNone(capture, "The caller's render target must be preserved")
        guarded = re.search(r"\btry\s*\{", body)
        self.assertIsNotNone(guarded, "Transient allocations/readback need failure cleanup")
        try_end = balanced_end(body, guarded.end() - 1, "{", "}")
        try_body = body[guarded.end():try_end]
        final = re.match(r"\s*finally\s*\{", body[try_end + 1:])
        self.assertIsNotNone(final, "All exits from readback must enter cleanup")
        final_start = try_end + final.end()
        final_end = balanced_end(body, final_start, "{", "}")
        cleanup = body[final_start + 1:final_end]
        self.assertLess(capture.start(), guarded.start())
        for allocation in ("new CommandBuffer", "new RenderTexture", "new Texture2D"):
            self.assertIn(allocation, try_body)
        for operation in ("Graphics.ExecuteCommandBuffer", "stagingTexture.ReadPixels",
                          "stagingTexture.GetRawTextureData"):
            self.assertIn(operation, try_body)
        restore = "RenderTexture.active = " + capture[1] + ";"
        self.assertIn(restore, cleanup)
        for release in ("buf?.Release();", "bakedWaves.Release();",
                        "Helpers.Destroy(bakedWaves);", "Helpers.Destroy(stagingTexture);"):
            self.assertIn(release, cleanup)
            self.assertLess(cleanup.index(restore), cleanup.index(release))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--ref")
    arguments, remaining = parser.parse_known_args()
    regression.SOURCE_REF = arguments.ref
    print("Wave spectrum: source equations, loop and IEEE phase invariants; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
