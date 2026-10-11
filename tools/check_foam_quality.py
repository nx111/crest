#!/usr/bin/env python3
"""Foam numerical regressions evaluated from the actual shader expressions.

The equations are checked against analytic ODE solutions and sampling bounds.
This is double-precision CPU evaluation, not shader compilation or GPU execution.
Use --ref <commit> to evaluate a previous source revision.
"""

import argparse
import ast
import math
from pathlib import Path
import re
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[1]
SHADERS = "crest/Assets/Crest/Crest/Shaders/"
SOURCE_REF = None


def source(path):
    if SOURCE_REF:
        result = subprocess.check_output(["git", "show", f"{SOURCE_REF}:{path}"], cwd=ROOT).decode("utf-8-sig")
    else:
        result = (ROOT / path).read_text(encoding="utf-8-sig")
    return re.sub(r"/\*.*?\*/|//[^\n]*", "", result, flags=re.S)


def braced(text, start):
    """Return a brace-delimited block and the next character index."""
    assert text[start] == "{"
    depth = 1
    for index in range(start + 1, len(text)):
        depth += (text[index] == "{") - (text[index] == "}")
        if depth == 0:
            return text[start + 1:index], index + 1
    raise AssertionError("Unclosed shader block")


def body(text, name):
    match = re.search(r"\b" + name + r"\s*\([^{}]*\)\s*\{", text)
    if match is None:
        raise AssertionError(f"Missing shader helper: {name}")
    return braced(text, match.end() - 1)[0]


class Vec(tuple):
    def __mul__(self, scale):
        return Vec(value * scale for value in self)

    __rmul__ = __mul__

    def __truediv__(self, scale):
        return self * (1.0 / scale)

    @property
    def x(self):
        return self[0]

    @property
    def y(self):
        return self[1]

    @property
    def z(self):
        return self[2]

    @property
    def xz(self):
        return Vec((self[0], self[2]))


def saturate(value):
    return min(1.0, max(0.0, value))


def smoothstep(lower, upper, value):
    weight = saturate((value - lower) / (upper - lower))
    return weight * weight * (3.0 - 2.0 * weight)


MATH = {
    "max": max, "min": min, "abs": abs, "exp": math.exp, "log2": math.log2,
    "saturate": saturate, "smoothstep": smoothstep,
    "dot": lambda a, b: sum(x * y for x, y in zip(a, b)),
    "float2": lambda x, y: Vec((x, y)),
}


def evaluate(expression, values):
    expression = expression.replace("&&", " and ").replace("||", " or ").strip()
    tree = ast.parse(expression, mode="eval")
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.BoolOp, ast.Call,
               ast.Name, ast.Load, ast.Constant, ast.Add, ast.Sub, ast.Mult,
               ast.Div, ast.USub, ast.Attribute, ast.Compare, ast.Gt, ast.Lt,
               ast.GtE, ast.LtE, ast.And, ast.Or)
    context = dict(MATH, **values)
    for node in ast.walk(tree):
        if not isinstance(node, allowed):
            raise AssertionError(f"Unsupported shader expression: {expression}")
        if isinstance(node, ast.Call) and (not isinstance(node.func, ast.Name) or not callable(context.get(node.func.id))):
            raise AssertionError(f"Unsupported function call: {expression}")
        if isinstance(node, ast.Attribute) and node.attr not in ("x", "y", "z", "xz"):
            raise AssertionError(f"Unsupported swizzle: {expression}")
    return eval(compile(tree, "<foam-shader-expression>", "eval"), {"__builtins__": {}}, context)


def execute(block, values):
    """Interpret the scalar/vector assignments and uniform if blocks used below."""
    while block.strip():
        block = block.lstrip()
        conditional = re.match(r"if\s*\(([^{}]+)\)\s*\{", block)
        if conditional:
            inner, end = braced(block, conditional.end() - 1)
            if evaluate(conditional[1], values):
                execute(inner, values)
            block = block[end:]
            continue
        statement, block = block.split(";", 1)
        statement = statement.strip()
        if statement.startswith("return "):
            return evaluate(statement[7:], values)
        statement = re.sub(r"^(?:const\s+)?(?:float[23]?|half)\s+", "", statement)
        match = re.fullmatch(r"(\w+)\s*([*+/-]?=)\s*(.+)", statement, flags=re.S)
        if match is None:
            raise AssertionError(f"Unsupported shader statement: {statement}")
        value = evaluate(match[3], values)
        name, op = match[1], match[2]
        if op == "=":
            values[name] = value
        else:
            values[name] = evaluate(f"previous {op[0]} nextValue", {"previous": values[name], "nextValue": value})


class FoamQualityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.compute = source(SHADERS + "Resources/UpdateFoam.compute")
        cls.surface = source(SHADERS + "OceanFoam.hlsl")
        cls.integration = body(cls.compute, "CrestFoamDecayAndSource")

    def weights(self, dt, rate):
        return execute(self.integration, {"deltaTime": dt, "fadeRate": rate})

    def test_decay_and_source_match_analytic_solution(self):
        for dt in (0.0, 1 / 200, 1 / 60, 1 / 30, 1 / 15, 0.25, 1.0):
            for rate in (0.0, 1e-8, 0.0001, 0.8, 5.0, 20.0):
                loss, accumulation = self.weights(dt, rate)
                expected_source = -math.expm1(-rate * dt) / rate if rate else dt
                self.assertAlmostEqual(loss, math.exp(-rate * dt), delta=1e-13)
                self.assertAlmostEqual(accumulation, expected_source, delta=max(1e-13, dt * 1e-9))
                self.assertTrue(0 <= loss <= 1)
                self.assertTrue(0 <= accumulation <= dt)

    def test_simulation_frequency_preserves_lifetime_and_build_up(self):
        for rate in (0.0, 0.8, 5.0, 20.0):
            for production in (0.0, 0.05, 0.15):
                duration, initial = 2.0, 0.15
                expected = initial * math.exp(-rate * duration)
                expected += production * (-math.expm1(-rate * duration) / rate if rate else duration)
                for frequency in (15, 30, 60, 200):
                    decay, source_weight = self.weights(1.0 / frequency, rate)
                    foam = initial
                    for _ in range(int(duration * frequency)):
                        foam = foam * decay + production * source_weight
                    self.assertAlmostEqual(foam, expected, delta=1e-10,
                                           msg=(rate, production, frequency))

    def test_partitioned_steps_agree_and_large_fades_retain_history(self):
        for dt, rate in ((1 / 15, 20.0), (0.25, 20.0), (1.0, 0.8)):
            decay, accumulation = self.weights(dt, rate)
            sub_decay, sub_accumulation = self.weights(dt / 5, rate)
            foam = 0.4
            for _ in range(5):
                foam = foam * sub_decay + 0.3 * sub_accumulation
            self.assertAlmostEqual(foam, 0.4 * decay + 0.3 * accumulation, delta=1e-12)
            self.assertGreater(decay, 0.0)

    def test_real_shader_sources_use_integrated_weights_and_zero_fade_is_finite(self):
        # Execute the real production update section and both source additions.
        start = self.compute.index("const float2 decayAndSource =")
        end = self.compute.index("const float wavesSliceIndex =", start)
        update = self.compute[start:end]
        additions = re.findall(r"foam \+= [^;]+;", self.compute)
        self.assertEqual(len(additions), 2)
        for prewarm in (False, True):
            for dt in (0.0, 1 / 15, 1 / 30, 1 / 200):
                for rate in (0.0, 1e-8, 0.8, 20.0):
                    values = {
                        "foam": 0.25, "_SimDeltaTime": dt, "_FoamFadeRate": rate,
                        "_NeedsPrewarming": prewarm, "_WaveFoamStrength": 0.1,
                        "_WaveFoamCoverage": 0.55, "det": 0.5, "foamBase": 0.0,
                        "_ShorelineFoamStrength": 0.2, "signedOceanDepth": 0.5,
                        "_ShorelineFoamMaxDepth": 1.0,
                        "CrestFoamDecayAndSource": self.weights,
                    }
                    execute(update + "\n" + "\n".join(additions), values)
                    self.assertTrue(math.isfinite(values["foam"]))
                    if not prewarm or rate == 0.0:
                        decay, source_weight = self.weights(dt, rate)
                        # Independent source rates from the fixture's compression and depth.
                        self.assertAlmostEqual(values["foam"], 0.25 * decay + 0.125 * source_weight, delta=1e-12)

    def test_bubble_mip_covers_pixel_footprint(self):
        shader = body(self.surface, "CrestBubbleFoamLod")
        for size in (128, 512, 2048):
            for footprint in (0.0, 0.25, 1.0, 8.0, 16.0, 64.0, 512.0):
                for horizontal in (False, True):
                    dx = Vec((footprint / size, 0.0)) if horizontal else Vec((0.0, 0.0))
                    dy = Vec((0.0, 0.0)) if horizontal else Vec((0.0, footprint / size))
                    lod = execute(shader, {"uv": Vec((0.25, 0.5)), "textureSize": size,
                                           "ddx": lambda uv: dx, "ddy": lambda uv: dy})
                    self.assertGreaterEqual(2 ** lod, footprint)
                    self.assertAlmostEqual(lod, max(3.0, math.log2(footprint) if footprint else 0.0), delta=1e-12)

    def test_grazing_parallax_is_bounded_and_continuous(self):
        expression = re.search(r"float2 parallaxOffset\s*=\s*([^;]+);", self.surface)[1]
        for parallax in (0.0, 0.14, 0.5):
            offsets = []
            for cosine in (-1.0, -0.5, -0.1, -1e-8, 0.0, 1e-8, 0.1, 0.5, 1.0):
                view = Vec((math.sqrt(1 - cosine * cosine), cosine, 0.0))
                offset = evaluate(expression, {"_FoamBubbleParallax": parallax,
                                               "i_view": view, "i_n": Vec((0.0, 1.0, 0.0))})
                self.assertTrue(all(math.isfinite(component) for component in offset))
                self.assertLessEqual(math.sqrt(sum(v * v for v in offset)), 10 * parallax + 1e-12)
                offsets.append(offset)
            self.assertAlmostEqual(offsets[3].x, offsets[5].x, delta=1e-10)

    def test_foam_threshold_is_finite_and_empty_water_stays_empty(self):
        shader = body(self.surface, "WhiteFoamTexture")
        shader = shader[shader.index("i_foam = saturate("):]
        for amount in (0.0, 0.1, 0.5, 1.0):
            for feather in (0.0, 0.001, 0.4, 1.0):
                for footprint in (0.0, 0.01, 0.5, 2.0):
                    previous = 0.0
                    for index in range(101):
                        value = execute(shader, {"i_foam": amount, "ft": index / 100,
                                                 "_WaveFoamFeather": feather,
                                                 "fwidth": lambda value: footprint})
                        self.assertTrue(math.isfinite(value) and 0.0 <= value <= 1.0)
                        self.assertGreaterEqual(value, previous)
                        previous = value
                        if amount == 0.0:
                            self.assertEqual(value, 0.0)

    def test_wave_filter_cannot_read_beyond_active_cascades(self):
        expression = re.search(r"const float wavesSliceIndex\s*=\s*([^;]+);", self.compute)[1]
        for depth in (2, 3, 7, 15):
            for desired in (0, 1, 6, 14, 99):
                for current in range(depth):
                    value = evaluate(expression, {"_CrestMinimumWavesSlice": desired,
                                                  "id": Vec((0, 0, current)), "depth": depth})
                    self.assertTrue(0 <= value < depth)
                    if desired < depth:
                        self.assertEqual(value, max(desired, current))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read shader sources from this Git revision")
    args, remaining = parser.parse_known_args()
    SOURCE_REF = args.ref
    print("Foam regressions: analytic/sampling checks of source expressions; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
