#!/usr/bin/env python3
"""Source-driven flow phase, continuity and visible coverage checks.

These execute the four production phase formulas with CPU arithmetic and pass
their weights through the actual foam blend. They do not render textures or
emulate GPU execution. Use --ref <commit> to demonstrate a previous regression.
"""
import argparse
from functools import lru_cache
import math
import re
import sys
import unittest

import check_optical_quality as equations
import check_urp_regressions as regression

HELPERS = regression.CREST + "Shaders/OceanHelpersNew.hlsl"
PATHS = {
    "normals": (regression.CREST + "Shaders/OceanNormalMapping.hlsl", "ApplyNormalMapsWithFlow"),
    "foam": (regression.CREST + "Shaders/OceanFoam.hlsl", "ComputeFoamWithFlow"),
    "combine_compute": (regression.CREST + "Shaders/Resources/ShapeCombine.compute", "Flow"),
    "combine_fragment": (regression.CREST + "Shaders/Resources/ShapeCombine.shader", "Flow"),
}
Vector = equations.Vector


@lru_cache(maxsize=None)
def function_body(path, name):
    return regression.body(regression.source(path), name)


def flow_phase(time, period):
    """Execute the shared helper rather than substituting a corrected formula."""
    return run_assignments(function_body(HELPERS, "CrestFlowPhase"), {"time": time, "period": period})


def evaluate(expression, values):
    # This is the exact indexed field used by both combine paths; the test
    # supplies the cascade's positive texel width instead of emulating a buffer.
    expression = expression.replace("_CrestCascadeData[_LD_SliceIndex]._texelWidth", "texelWidth")
    return equations.evaluate(expression, values, {
        "fmod": lambda value, period: Vector(math.fmod(component, period) for component in value)
        if isinstance(value, tuple) else math.fmod(value, period),
        "float2": lambda *components: Vector(components),
        "CrestFlowPhase": flow_phase,
    })


def run_assignments(body, values):
    """Interpret only the assignments and single-line if in the phase prefix.

    Unknown statements fail explicitly. No shader equation is replaced by a
    Python implementation, so --ref also evaluates the original signed fmod.
    """
    values = dict(values)
    for statement in body.split(";"):
        statement = statement.strip()
        if not statement: continue
        if statement.startswith("return "): return evaluate(statement[7:], values)
        condition = re.fullmatch(r"if\s*\(([^()]*)\)\s*(.+)", statement, re.S)
        if condition:
            if not evaluate(condition[1], values): continue
            statement = condition[2]
        match = re.fullmatch(r"(?:(?:const\s+)?(?:float|half)[234]?\s+)?"
                             r"(\w+(?:\.[xy])?)\s*(=|-=)\s*(.+)", statement, re.S)
        if match is None: raise AssertionError(f"Unsupported flow statement: {statement}")
        name, operation, expression = match.groups()
        value = evaluate(expression, values)
        if "." in name:
            vector, component = name.split(".")
            index = "xy".index(component)
            old = list(values.get(vector, Vector((0, 0))))
            old[index] = value if operation == "=" else old[index] - value
            values[vector] = Vector(old)
        else:
            values[name] = value if operation == "=" else values[name] - value
    return values


@lru_cache(maxsize=None)
def phase_prefix(name):
    body = function_body(*PATHS[name])
    # Sampling begins after the phase/weight prefix in the two surface paths.
    # The combine helpers consist entirely of the phase/weight calculation.
    return re.split(r"\bhalf[234]?\s+(?:io_n_1|o_bubbleCol1)\b", body, maxsplit=1)[0]


def phases(name, time, texel_width=1):
    values = run_assignments(phase_prefix(name), {"_CrestTime": time, "texelWidth": texel_width})
    if "sample1_offset" in values:
        return (Vector((values["sample1_offset"], values["sample2_offset"])),
                Vector((values["sample1_weight"], values["sample2_weight"])))
    return values["offsets"], values["weights"]


def configurations():
    for name in PATHS:
        for width in ((1,) if name in ("normals", "foam") else (.125, 1, 7.5, 10.24)):
            yield name, width, 2 if name in ("normals", "foam") else max(3 * width, 1)


class FlowSampling(unittest.TestCase):
    def assertVectorClose(self, actual, expected, tolerance=1e-10):
        self.assertEqual(len(actual), len(expected))
        for value, reference in zip(actual, expected):
            self.assertAlmostEqual(value, reference, delta=tolerance * max(1, abs(reference)))

    def test_signed_time_keeps_phase_weights_and_sample_blends_bounded(self):
        for name, width, period in configurations():
            for fraction in (-10, -3.999, -2, -1.75, -1, -.75, -.5, -.25, -1e-8,
                             0, 1e-8, .25, .5, .999, 1, 2.875, 33):
                with self.subTest(path=name, width=width, time=fraction * period):
                    offsets, weights = phases(name, fraction * period, width)
                    low = -.5 * period if name == "normals" else 0
                    for offset in offsets:
                        self.assertTrue(math.isfinite(offset))
                        self.assertGreaterEqual(offset, low)
                        # A tiny negative remainder can round to the upper end
                        # when adding a period. Its phase has zero blend weight.
                        self.assertLessEqual(offset, low + period)
                    for weight in weights:
                        self.assertTrue(math.isfinite(weight))
                        self.assertGreaterEqual(weight, 0)
                        self.assertLessEqual(weight, 1)
                    self.assertAlmostEqual(sum(weights), 1)
                    mixed = weights.x * Vector((-2, .1, 4)) + weights.y * Vector((3, .8, -1))
                    for value, a, b in zip(mixed, (-2, .1, 4), (3, .8, -1)):
                        self.assertGreaterEqual(value, min(a, b))
                        self.assertLessEqual(value, max(a, b))

    def test_nonnegative_time_retains_original_sampling_and_weights(self):
        for name, width, period in configurations():
            for time in (0, .125, .3, .999, 1.001, 1.8, 9.4, 1000000.125):
                with self.subTest(path=name, width=width, time=time):
                    # Independent compatibility reference, deliberately using
                    # the old positive-time formula and normal-map centering.
                    offsets = Vector((math.fmod(time, period), math.fmod(time + period / 2, period)))
                    weight = offsets.x / (period / 2)
                    weight = 2 - weight if weight > 1 else weight
                    if name == "normals": offsets -= period / 2
                    actual_offsets, actual_weights = phases(name, time, width)
                    self.assertVectorClose(actual_offsets, offsets)
                    self.assertVectorClose(actual_weights, (weight, 1 - weight))

    def test_all_four_paths_repeat_over_negative_and_positive_periods(self):
        for name, width, period in configurations():
            # Interior points avoid representing an exact decimal wrap boundary
            # on opposite sides due to host binary floating-point roundoff.
            for fraction in (.125, .25, .5, .75, .875):
                expected_offsets, expected_weights = phases(name, fraction * period, width)
                for cycle in (-9, -2, -1, 0, 1, 5):
                    with self.subTest(path=name, width=width, fraction=fraction, cycle=cycle):
                        offsets, weights = phases(name, (cycle + fraction) * period, width)
                        self.assertVectorClose(offsets, expected_offsets)
                        self.assertVectorClose(weights, expected_weights)

    def test_visible_blend_is_continuous_across_both_phase_resets(self):
        for name, width, period in configurations():
            def sample(time):
                offsets, weights = phases(name, time, width)
                # A bounded smooth spatial signal, deliberately not periodic in
                # the reset distance. Continuity must come from phase fading.
                values = [math.sin(.7 + 1.7 * offset / period) + .2 * offset / period for offset in offsets]
                return sum(weight * value for weight, value in zip(weights, values))

            for half_cycle in range(-8, 9):
                boundary = half_cycle * period / 2
                with self.subTest(path=name, width=width, boundary=boundary):
                    centre = sample(boundary)
                    for side in (-1, 1):
                        self.assertAlmostEqual(sample(boundary + side * period * 1e-7), centre, delta=5e-6)

    def test_signed_flow_time_keeps_actual_foam_coverage_and_colour_bounded(self):
        from check_foam_shading import flow_blend, stored_float_blend
        pairs = ((Vector((1, 1, 1, 1)), Vector((0, 0, 0, 0))),
                 (Vector((0, 0, 0, 0)), Vector((1, .5, .25, 1))),
                 (Vector((.8, .2, .1, .25)), Vector((.1, .6, .9, .75))))
        for time in (-7.5, -3.5, -1.5, -.5, -.25, 0, .25, .5, 1.5, 3.5, 7.5):
            weight = phases("foam", time)[1].x
            # Python's Euclidean modulo is an independent mathematical oracle.
            expected_weight = time % 2
            if expected_weight > 1: expected_weight = 2 - expected_weight
            for first, second in pairs:
                with self.subTest(time=time, first=first, second=second):
                    result = flow_blend(first, second, weight)
                    for component in result:
                        self.assertTrue(math.isfinite(component))
                        self.assertGreaterEqual(component, 0)
                        self.assertLessEqual(component, 1)
                    expected_coverage = expected_weight * first.a + (1 - expected_weight) * second.a
                    expected_radiance = expected_weight * first.a * first.rgb + (1 - expected_weight) * second.a * second.rgb
                    self.assertAlmostEqual(result.a, expected_coverage)
                    self.assertVectorClose(result.rgb * result.a, expected_radiance)
                    # Existing foam storage model rounds float intermediates and
                    # the half4 result; this catches the former half overflow.
                    for component in stored_float_blend(first, second, weight):
                        self.assertTrue(math.isfinite(component))
                        self.assertGreaterEqual(component, 0)
                        self.assertLessEqual(component, 1)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--ref")
    arguments, remaining = parser.parse_known_args()
    regression.SOURCE_REF = arguments.ref
    print("Flow sampling: production phases and coverage invariants; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
