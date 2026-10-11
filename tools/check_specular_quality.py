#!/usr/bin/env python3
"""Evaluate actual scalar HLSL functions against shading invariants (no GPU)."""
import ast
import math
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
SHADERS = ROOT / "crest/Assets/Crest/Crest/Shaders"


def scalar_function(name, *arguments):
    text = (SHADERS / "OceanSpecular.hlsl").read_text()
    text = re.sub(r"//[^\n]*", "", text)
    match = re.search(r"float " + name + r"\(([^)]*)\)\s*\{([^}]*)\}", text)
    names = [parameter.strip().split()[-1] for parameter in match[1].split(",")]
    values = dict(zip(names, arguments))
    functions = {"saturate": lambda x: min(max(x, 0.0), 1.0), "sqrt": math.sqrt, "pow": pow, "max": max}

    def expression(text):
        tree = ast.parse(text.strip(), mode="eval")
        allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Call, ast.Name, ast.Load,
                   ast.Constant, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.USub)
        for node in ast.walk(tree):
            if not isinstance(node, allowed):
                raise AssertionError(f"Unsupported scalar HLSL expression: {text}")
            if isinstance(node, ast.Call) and (not isinstance(node.func, ast.Name) or node.func.id not in functions):
                raise AssertionError("Unsupported HLSL call")
        return eval(compile(tree, "<HLSL>", "eval"), {"__builtins__": {}}, dict(functions, **values))

    for statement in match[2].split(";"):
        statement = statement.strip()
        if not statement:
            continue
        if statement.startswith("return "):
            return expression(statement[7:])
        assignment = re.fullmatch(r"(?:float\s+)?(\w+)\s*(=|\*=)\s*(.+)", statement, re.S)
        if assignment is None:
            raise AssertionError(f"Unsupported HLSL statement: {statement}")
        name, operation, value = assignment.groups()
        values[name] = expression(value) * (values[name] if operation == "*=" else 1.0)
    raise AssertionError("Missing shader return")


def roughness(value, variance):
    return scalar_function("CrestFilteredPerceptualRoughness", value, variance)


def highlight(cosine, exponent, variance):
    return scalar_function("CrestFilteredLightHighlight", cosine, exponent, variance)


class SpecularQuality(unittest.TestCase):
    def test_zero_variance_preserves_authored_roughness_and_highlights(self):
        for p in (0.0, 0.01, 0.15, 0.4, 0.9, 1.0):
            self.assertAlmostEqual(roughness(p, 0.0), p)
        for exponent in (1.0, 42.0, 275.0, 4096.0):
            for cosine in (0.0, 0.3, 0.8, 0.95, 0.999, 1.0):
                self.assertAlmostEqual(highlight(cosine, exponent, 0.0), cosine ** exponent)

    def test_variance_is_applied_to_physical_roughness_squared(self):
        # Unity p -> alpha = p^2; isotropic normal variance contributes 2*v to alpha^2.
        # Checking the inverse mapping catches an erroneous p^2 rather than p^4 filter.
        for p in (0.0, 0.2, 0.5, 0.8):
            for variance in (0.00001, 0.001, 0.02):
                self.assertAlmostEqual(roughness(p, variance) ** 4 - p ** 4, 2.0 * variance)

    def test_roughness_never_decreases_and_remains_finite_at_limits(self):
        for p in (-1.0, 0.0, 0.01, 0.2, 0.6, 0.99, 1.0, 2.0):
            previous = min(max(p, 0.0), 1.0)
            for variance in (0.0, 0.00001, 0.001, 0.02, 0.5):
                actual = roughness(p, variance)
                self.assertTrue(math.isfinite(actual))
                self.assertGreaterEqual(actual + 1e-12, previous)
                self.assertLessEqual(actual, 1.0)
                previous = actual

    def test_filtered_highlight_preserves_angular_lobe_integral(self):
        # Independent quadrature over cos(theta), where d(omega)=2*pi*d(cos(theta)).
        # Simpson integration observes the entire lobe, not just its changing peak.
        intervals = 4096
        for exponent in (1.0, 42.0, 275.0):
            for variance in (0.0001, 0.005, 0.02):
                samples = [highlight(i / intervals, exponent, variance) for i in range(intervals + 1)]
                integral = (samples[0] + samples[-1] + 4 * sum(samples[1:-1:2])
                            + 2 * sum(samples[2:-1:2])) / (3 * intervals)
                expected = 1.0 / (exponent + 1.0)
                self.assertAlmostEqual(integral, expected, delta=expected * 0.0001)

    def test_filter_reduces_peak_without_negative_or_nonfinite_response(self):
        for exponent in (1.0, 42.0, 275.0, 4096.0):
            previous = 1.0
            for variance in (0.0, 0.0001, 0.001, 0.02, 0.5):
                peak = highlight(1.0, exponent, variance)
                self.assertLessEqual(peak, previous)
                previous = peak
                for cosine in (-1.0, 0.0, 0.5, 0.99, 1.0, 1.01):
                    actual = highlight(cosine, exponent, variance)
                    self.assertTrue(math.isfinite(actual))
                    self.assertGreaterEqual(actual, 0.0)
                    self.assertLessEqual(actual, 1.0)

    def test_derivatives_precede_face_branch_and_light_loops(self):
        shader = (SHADERS / "Ocean.shader").read_text()
        assignment = shader.index("float normalVariance = CrestSpecularNormalVariance(n_pixel);")
        self.assertLess(assignment, shader.index("if (underwater) n_pixel = -n_pixel;"))
        self.assertLess(assignment, shader.index("CrestAdditionalLights(input.worldPos"))
        lights = (SHADERS / "OceanAdditionalLights.hlsl").read_text()
        self.assertNotRegex(lights, r"\b(?:ddx|ddy|CrestSpecularNormalVariance)\s*\(")


if __name__ == "__main__":
    print("Specular quality: actual scalar shader expressions; no Unity/GPU execution.", flush=True)
    unittest.main()
