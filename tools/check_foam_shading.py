#!/usr/bin/env python3
"""Evaluate foam shading source against independent coverage/light invariants.

CPU equations, source contracts and a storage-rounded IEEE float model only.
This does not compile shaders, render images or measure GPU performance.
Use --ref <commit> to evaluate the production source from an earlier revision.
"""
import argparse
from functools import lru_cache
import math
import re
import struct
import sys
import unittest

import check_reflection_quality as shader
from check_optical_quality import Vector

FOAM = shader.regression.CREST + "Shaders/OceanFoam.hlsl"
SPECULAR = shader.regression.CREST + "Shaders/OceanSpecular.hlsl"


def constructor(*arguments):
    return Vector(component for argument in arguments
                  for component in (argument if isinstance(argument, tuple) else (argument,)))


shader.FUNCTIONS.update({
    "float2": constructor, "half4": constructor, "fwidth": lambda _: 0,
    "ddx": lambda _: Vector((0, 0, 0)), "ddy": lambda _: Vector((0, 0, 0)),
    "fmod": math.fmod,
    "reflect": lambda incident, normal: incident - 2 * shader.dot(incident, normal) * normal,
    "CrestWhiteFoamCoverage": lambda amount, value: coverage(amount, value),
    "CrestBlendWhiteFoam": lambda first, second, weight: shader.shader_function(FOAM, "CrestBlendWhiteFoam", first, second, weight),
    "CrestFilteredLightHighlight": lambda cosine, power, variance: shader.shader_function(
        SPECULAR, "CrestFilteredLightHighlight", cosine, power, variance),
})


def coverage(amount, value, feather=.4, footprint=0):
    values = {"i_foam": amount, "ft": value, "_WaveFoamFeather": feather, "fwidth": lambda _: footprint}
    if "CrestWhiteFoamCoverage" in shader.source(FOAM):
        body = shader.function_body(FOAM, "CrestWhiteFoamCoverage")
    else:
        body = shader.function_body(FOAM, "WhiteFoamTexture")
        body = body[body.index("i_foam = saturate("):]
    return shader.run_assignments(body, values)


@lru_cache(maxsize=None)
def white_foam_sampling_body():
    """Supply two texture values while executing the actual order of operations."""
    body = shader.function_body(FOAM, "WhiteFoamTexture")
    body = body[re.search(r"\bhalf\s+ft(?:0)?\s*=", body).start():]
    count = 0
    while match := re.search(r"\bi_texture\.Sample\(", body):
        end, depth = match.end(), 1
        while depth:
            depth += (body[end] == "(") - (body[end] == ")")
            end += 1
        channel = re.match(r"\s*\.r", body[end:])
        if channel is None: raise AssertionError("Unexpected foam texture channel")
        body = body[:match.start()] + f"texture{count}" + body[end + channel.end():]
        count += 1
    if count != 2: raise AssertionError("White foam must sample both cascades exactly once")
    return body


def white_foam(amount, first, second, lod, feather=.4, footprint=0):
    return shader.run_assignments(white_foam_sampling_body(), {
        "i_foam": amount, "texture0": first, "texture1": second, "lodVal": lod,
        "_WaveFoamFeather": feather, "fwidth": lambda _: footprint,
        "CrestWhiteFoamCoverage": lambda a, v: coverage(a, v, feather, footprint),
    })


def independent_coverage(amount, value, feather, footprint):
    threshold = min(max(1 - amount, 0), 1)
    width = max(feather, .001, footprint)
    fraction = min(max((value - threshold) / width, 0), 1)
    return fraction * fraction * (3 - 2 * fraction)


def flow_blend(first, second, weight):
    expression = shader.assignment(shader.function_body(FOAM, "ComputeFoamWithFlow"), "o_whiteFoamCol")
    return shader.evaluate(expression, {
        "o_whiteFoamCol1": first, "o_whiteFoamCol2": second,
        "sample1_weight": weight, "sample2_weight": 1 - weight,
    })


def sun_highlight(cosine, ndl=1, variance=0, shadow=1, power=293, directions=None):
    body = shader.function_body(FOAM, "ComputeFoam")
    values = {"refl": Vector((cosine, 0, 0)), "i_lightDir": Vector((1, 0, 0)), "fN": Vector((ndl, 0, 0)),
              "foamNormalVariance": variance, "_WaveFoamSpecularFallOff": power,
              "_WaveFoamSpecularBoost": 1, "_LightColor0": 1, "i_shadow": shadow}
    if directions is not None:
        values["fN"], values["i_view"], values["i_lightDir"] = directions
        values["refl"] = shader.evaluate(shader.assignment(body, "refl"), values)
    values["foamNdL"] = shader.evaluate(shader.assignment(body, "foamNdL"), values)
    expression = re.search(r"o_whiteFoamCol\.rgb\s*\+=\s*([^;]+);", body)[1]
    return shader.evaluate(expression, values)


def composite(background, sample):
    return background * (1 - sample.a) + sample.rgb * sample.a


def quantize(value, kind):
    if isinstance(value, tuple): return Vector(quantize(component, kind) for component in value)
    return struct.unpack(kind, struct.pack(kind, float(value)))[0]


def stored_float_blend(first, second, weight):
    """Round declared storage; this is not a GPU arithmetic/denormal emulator."""
    body = shader.function_body(FOAM, "CrestBlendWhiteFoam")
    values = dict(first=quantize(first, "e"), second=quantize(second, "e"), firstWeight=quantize(weight, "f"))
    for statement in body.split(";"):
        statement = statement.strip()
        if not statement: continue
        if statement.startswith("return "):
            return quantize(shader.evaluate(statement[7:], values), "e")
        match = re.fullmatch(r"(float[234]?|half[234]?)\s+(\w+)\s*=\s*(.+)", statement, re.S)
        if match is None: raise AssertionError(f"Unexpected blending statement: {statement}")
        values[match[2]] = quantize(shader.evaluate(match[3], values), "f" if match[1].startswith("float") else "e")
    raise AssertionError("Missing coverage blend return")


class FoamShading(unittest.TestCase):
    def assertVectorClose(self, actual, expected, tolerance=1e-10):
        for component, reference in zip(actual, expected):
            self.assertAlmostEqual(component, reference, delta=tolerance)

    def test_lod_transition_is_the_linear_blend_of_visible_coverage(self):
        for first, second in ((1, 0), (.4, .9), (.8, 1), (0, 0)):
            for amount in (.1, .3, .6, 1):
                for feather in (.001, .4):
                    for footprint in (0, .02, 1):
                        a = independent_coverage(amount, first, feather, footprint)
                        b = independent_coverage(amount, second, feather, footprint)
                        for lod in (0, .25, .5, .75, 1):
                            actual = white_foam(amount, first, second, lod, feather, footprint)
                            self.assertAlmostEqual(actual, a * (1 - lod) + b * lod, places=12)

    def test_black_point_empty_water_and_lod_endpoints_are_preserved(self):
        for first, second in ((0, 1), (.25, .75), (.5, .5), (1, 1)):
            for lod in (0, .01, .25, .5, .75, .99, 1):
                self.assertEqual(white_foam(0, first, second, lod), 0)
            for amount in (.1, .3, .7, 1):
                for lod, value in ((0, first), (1, second)):
                    self.assertAlmostEqual(white_foam(amount, first, second, lod),
                                           independent_coverage(amount, value, .4, 0), places=12)

    def test_texture_fetch_count_and_unconditional_derivative_flow(self):
        body = shader.function_body(FOAM, "WhiteFoamTexture")
        self.assertEqual(body.count("i_texture.Sample("), 2)
        self.assertEqual(body.count("CrestWhiteFoamCoverage("), 2)
        self.assertNotRegex(body, r"\bif\s*\(|\?")
        coverage_body = shader.function_body(FOAM, "CrestWhiteFoamCoverage")
        self.assertIn("fwidth(ft - i_foam)", coverage_body)
        self.assertNotRegex(coverage_body, r"\bif\s*\(|\?")
        compute = shader.function_body(FOAM, "ComputeFoam")
        self.assertEqual(compute.count("WhiteFoamTexture("), 3)
        flow = shader.function_body(FOAM, "ComputeFoamWithFlow")
        self.assertEqual(flow.count("ComputeFoam("), 2)

    def test_sun_filter_uses_foam_relief_and_existing_aa_controls(self):
        body = shader.function_body(FOAM, "ComputeFoam")
        self.assertIn("CrestSpecularNormalVariance(fN)", body)
        self.assertLess(body.index("foamNormalVariance ="), body.index("foamNdL ="))
        self.assertIn("_WaveFoamSpecularFallOff, foamNormalVariance)", body)
        peaks = []
        for scale in (0, .05, .15, .25):
            variance = shader.shader_function(SPECULAR, "CrestSpecularNormalVariance", Vector((0, 1, 0)),
                _SpecularAAVariance=scale, _SpecularAAThreshold=.2,
                ddx=lambda _: Vector((.2, 0, 0)), ddy=lambda _: Vector((0, 0, .1)))
            peaks.append(sun_highlight(1, variance=variance))
        self.assertEqual(peaks[0], 1)
        self.assertTrue(all(a > b for a, b in zip(peaks, peaks[1:])))

    def test_sun_highlights_respect_light_hemisphere_shadow_and_dot_roundoff(self):
        for variance in (0, .001, .02):
            for cosine in (-1, 0, .9, 1, 1.0009765625):
                for ndl in (-1, -.001, 0):
                    self.assertEqual(sun_highlight(cosine, ndl, variance), 0)
                self.assertEqual(sun_highlight(cosine, 1, variance, shadow=0), 0)
                actual = sun_highlight(cosine, 1.0009765625, variance)
                self.assertTrue(math.isfinite(actual) and 0 <= actual <= 1)

        # A valid front-facing view at a grazing angle and a sun just below the
        # surface can still have reflect(-V, N).L close to one. Its light is blocked.
        grazing = .005
        tangent = math.sqrt(1 - grazing * grazing)
        directions = (Vector((0, 1, 0)), Vector((tangent, grazing, 0)), Vector((-tangent, -grazing, 0)))
        self.assertEqual(sun_highlight(0, directions=directions), 0)

    def test_zero_variance_keeps_authored_phong_exponent_on_the_lit_side(self):
        for power in (1, 42, 293, 512):
            for cosine in (0, .3, .9, .99, 1):
                for ndl in (.25, .5, 1):
                    self.assertAlmostEqual(sun_highlight(cosine, ndl, power=power), cosine ** power * ndl, places=12)

    def test_flow_blend_matches_crossfade_of_final_composites(self):
        background = Vector((.07, .2, .4))
        for colour0, colour1 in (((1, .3, .1), (.2, .5, .9)), ((4, 2, .7), (.1, .1, .1))):
            for alpha0 in (0, .01, .3, 1):
                for alpha1 in (0, .02, .6, 1):
                    first, second = Vector((*colour0, alpha0)), Vector((*colour1, alpha1))
                    for weight in (0, .1, .5, .9, 1):
                        actual = flow_blend(first, second, weight)
                        self.assertAlmostEqual(actual.a, weight * alpha0 + (1 - weight) * alpha1)
                        expected = composite(background, first) * weight + composite(background, second) * (1 - weight)
                        self.assertVectorClose(composite(background, actual), expected)

    def test_flow_empty_phase_has_no_colour_or_highlight_influence(self):
        covered = Vector((.2, .4, .8, .7))
        for colour in ((0, 0, 0), (1, 1, 1), (15, 8, 4)):
            empty = Vector((*colour, 0))
            for weight in (.01, .25, .5, .99):
                self.assertVectorClose(flow_blend(empty, covered, weight).rgb, covered.rgb)
                self.assertVectorClose(flow_blend(covered, empty, weight).rgb, covered.rgb)

    def test_tiny_coverage_has_finite_float_intermediates_and_no_dark_nan_pixels(self):
        body = shader.function_body(FOAM, "CrestBlendWhiteFoam")
        self.assertIn("float2 coverageWeights", body)
        self.assertIn("float coverage", body)
        self.assertIn("float3 premultiplied", body)
        for alpha in (0, 2 ** -24, 2 ** -14, .001, 1):
            first, second = Vector((12, 3, .5, alpha)), Vector((.2, .4, .8, 0))
            for weight in (0, 1e-6, .001, .5, 1):
                actual = stored_float_blend(first, second, weight)
                self.assertTrue(all(math.isfinite(component) and component >= 0 for component in actual))
                self.assertLessEqual(actual.a, 1)
                if actual.a > 0: self.assertVectorClose(actual.rgb, first.rgb, tolerance=.008)

    def test_flow_phase_resets_remain_continuous(self):
        body = shader.function_body(FOAM, "ComputeFoamWithFlow")

        def sample(time):
            values = {"_CrestTime": time}
            for name in ("half_period", "period", "sample1_offset", "sample1_weight"):
                values[name] = shader.evaluate(shader.assignment(body, name), values)
            branch = re.search(r"if\s*\(([^()]*)\)\s*sample1_weight\s*=\s*([^;]+);", body)
            if shader.evaluate(branch[1], values): values["sample1_weight"] = shader.evaluate(branch[2], values)
            values["sample2_offset"] = shader.evaluate(shader.assignment(body, "sample2_offset"), values)

            def phase(offset):
                return Vector((.4 + .1 * math.sin(offset), .2, .8, .5 + .3 * math.cos(2 * offset)))

            return composite(Vector((.1, .2, .3)), flow_blend(phase(values["sample1_offset"]), phase(values["sample2_offset"]), values["sample1_weight"]))

        for reset in (1, 2, 3, 10):
            self.assertVectorClose(sample(reset - 1e-6), sample(reset + 1e-6), tolerance=2e-6)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read production source from this Git revision")
    args, remaining = parser.parse_known_args()
    shader.regression.SOURCE_REF = args.ref
    print("Foam shading: actual HLSL equations and source checks, with an IEEE storage model; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
