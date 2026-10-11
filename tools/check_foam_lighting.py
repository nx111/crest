#!/usr/bin/env python3
"""Check actual foam light/data-flow equations against covered-area lighting.

Synthetic normals and coverage stand in for texture samples. These tests execute
the production light equations and the phase-data assignments, including the old
water-normal path with --ref. They do not emulate texture filtering, GPU half/FMA
arithmetic, URP light fetching, Unity compilation or rendering. Selected weight
cases round declared float storage to IEEE binary32.
"""
import argparse
from functools import lru_cache
import math
import re
import sys
import unittest

import check_foam_shading as shading
from check_normal_sampling import calls
from check_optical_quality import Vector, dot, length

shader = shading.shader
FOAM = shading.FOAM
LIGHTS = shader.regression.CREST + "Shaders/OceanAdditionalLights.hlsl"
DEFAULTS = {
    "_FoamWhiteColor": Vector((.9, .8, .7, 1)), "_WaveFoamLightScale": 1.2,
    "_WaveFoamSpecularBoost": 2, "_WaveFoamSpecularFallOff": 293,
    "_AdditionalLightIntensity": 1, "_AdditionalLightBoost": 1.5, "_AdditionalLightFallOff": 42,
}
ZERO = Vector((0, 0, 0))
UP = Vector((0, 1, 0))


def normalize(value):
    return Vector(value) / length(value)


def select_branches(text, keywords):
    """Select the boolean keyword branches in the local-light accumulator."""
    active, stack, output = True, [], []
    for line in text.splitlines():
        stripped = line.strip()
        if stripped.startswith("#if "):
            condition = stripped[4:]
            condition = re.sub(r"defined\((\w+)\)", r"\1", condition)
            if not re.fullmatch(r"[\w\s!&|]+", condition):
                raise AssertionError(f"Unsupported keyword condition: {condition}")
            enabled = any(all((part.strip()[1:] not in keywords if part.strip().startswith("!")
                               else part.strip() in keywords) for part in term.split("&&"))
                          for term in condition.split("||"))
            stack.append((active, enabled))
            active = active and enabled
        elif stripped == "#else":
            parent, enabled = stack[-1]
            active = parent and not enabled
        elif stripped == "#endif":
            active, _ = stack.pop()
        elif stripped.startswith("#"):
            raise AssertionError(f"Unsupported keyword directive: {stripped}")
        elif active:
            output.append(line)
    if stack:
        raise AssertionError("Unclosed keyword branch")
    return "\n".join(output)


def equation(expression, values):
    # Bind only the supplied struct fields, leaving vector swizzles to the shared
    # restricted expression evaluator. No arbitrary Python attribute access.
    names = sorted((name for name in values if "." in name), key=len, reverse=True)
    for name in names:
        expression = expression.replace(name, name.replace(".", "_"))
    expression = re.sub(r"(?<=\d)h\b", "", expression)
    return shader.evaluate(expression, {name.replace(".", "_"): value for name, value in values.items()})


def phase_data(normal, variance, alpha):
    data = {"normal0": ZERO, "normal1": ZERO, "variance0": 0, "variance1": 0, "weights": Vector((0, 0))}
    body = shader.function_body(FOAM, "ComputeFoam")
    if "o_foamLighting.normal0" not in body:
        return data  # Earlier source's light accumulator uses its water normal.
    values = {"fN": normal, "foamNormalVariance": variance, "o_whiteFoamCol": Vector((0, 0, 0, alpha))}
    for name in ("normal0", "variance0", "weights"):
        data[name] = equation(shader.assignment(body, "o_foamLighting." + name), values)
    return data


def flow_data(first, second, alpha0, alpha1, weight, binary32=False):
    body = shader.function_body(FOAM, "ComputeFoamWithFlow")
    if "o_foamLighting.normal0" not in body:
        return first
    values = {"sample1_weight": weight, "sample2_weight": 1 - weight,
              "o_whiteFoamCol1": Vector((0, 0, 0, alpha0)), "o_whiteFoamCol2": Vector((0, 0, 0, alpha1))}
    values.update({f"foamLighting{index}.{key}": value for index, data in ((1, first), (2, second)) for key, value in data.items()})
    result = {}
    for name in ("o_foamLighting.normal0", "o_foamLighting.normal1", "o_foamLighting.variance0", "o_foamLighting.variance1",
                 "coverageWeights", "coverage", "o_foamLighting.weights"):
        value = equation(shader.assignment(body, name), values)
        if binary32 and (name in ("coverageWeights", "coverage", "o_foamLighting.weights")):
            is_float = (re.search(r"\bfloat2\s+weights\s*;", shader.source(FOAM)) if name.startswith("o_foamLighting")
                        else re.search(r"\bfloat[234]?\s+" + name + r"\s*=", body))
            kind = "f" if is_float else "e"
            value = shading.quantize(value, kind)
        values[name] = value
        if name.startswith("o_foamLighting."):
            result[name.split(".")[1]] = value
    return result


shader.FUNCTIONS["CrestFoamAdditionalLight"] = lambda *args: None


@lru_cache(maxsize=None)
def accumulator(foam, relief, flow):
    keywords = {name for name, enabled in (("_FOAM_ON", foam), ("_FOAM3DLIGHTING_ON", relief), ("_FLOW_ON", flow)) if enabled}
    return select_branches(shader.function_body(LIGHTS, "CrestAccumulateAdditionalLight"), keywords)


def light_response(data, direction, view=UP, water=UP, water_variance=0, colour=Vector((1, 1, 1)),
                   distance=1, shadow=1, foam=True, relief=True, flow=False, **settings):
    values = {**DEFAULTS, **settings, "normalWS": water, "view": view, "normalVariance": water_variance,
              "light.direction": direction, "light.color": colour,
              "light.distanceAttenuation": distance, "light.shadowAttenuation": shadow}
    values.update({"foamLighting." + key: value for key, value in data.items()})
    values["CrestFoamAdditionalLight"] = lambda *args: shader.shader_function(
        LIGHTS, "CrestFoamAdditionalLight", *args, **{**DEFAULTS, **settings})
    body = accumulator(foam, relief, flow)
    values["radiance"] = equation(shader.assignment(body, "radiance"), values)
    values["ndl"] = equation(shader.assignment(body, "ndl"), values)
    result = ZERO
    for match in re.finditer(r"(?:if\s*\(([^()]*)\)\s*\{\s*)?\bfoam\s*\+=\s*([^;]+);", body):
        if match[1] is None or equation(match[1], values):
            result += equation(match[2], values)
    return result


def reference_light(normal, variance, direction, view=UP, colour=Vector((1, 1, 1)), distance=1, shadow=1, **settings):
    """Independent normalized Phong lobe and positive-hemisphere diffuse model."""
    settings = {**DEFAULTS, **settings}
    ndl = max(0, min(1, dot(normal, direction)))
    reflected = normal * (2 * dot(view, normal)) - view
    cosine = max(0, min(1, dot(reflected, direction)))
    power = max(1, settings["_WaveFoamSpecularFallOff"])
    filtered_power = power / (1 + 4 * variance * power)
    highlight = cosine ** filtered_power * (filtered_power + 1) / (power + 1)
    radiance = colour * (distance * shadow * settings["_AdditionalLightIntensity"])
    return (settings["_FoamWhiteColor"].rgb * settings["_WaveFoamLightScale"]
            + highlight * settings["_WaveFoamSpecularBoost"]) * radiance * ndl


def flow_timing(time):
    body = shader.function_body(FOAM, "ComputeFoamWithFlow")
    values = {"_CrestTime": time}
    for name in ("half_period", "period", "sample1_offset", "sample1_weight"):
        values[name] = equation(shader.assignment(body, name), values)
    branch = re.search(r"if\s*\(([^()]*)\)\s*sample1_weight\s*=\s*([^;]+);", body)
    if equation(branch[1], values):
        values["sample1_weight"] = equation(branch[2], values)
    values["sample2_offset"] = equation(shader.assignment(body, "sample2_offset"), values)
    return values["sample1_offset"], values["sample2_offset"], values["sample1_weight"]


class FoamLighting(unittest.TestCase):
    def assertVector(self, actual, expected, tolerance=1e-11):
        for a, e in zip(actual, expected):
            self.assertAlmostEqual(a, e, delta=tolerance)

    def test_single_phase_uses_the_sun_foam_normal_on_tilted_relief(self):
        for normal in (UP, normalize((1, 1, 0)), normalize((-.3, 1, .7))):
            for light in (UP, normalize((1, 0, 0)), normalize((-.2, .5, 1))):
                for variance in (0, .001, .02):
                    data = phase_data(normal, variance, .7)
                    actual = light_response(data, light)
                    self.assertVector(actual, reference_light(normal, variance, light))
                    # The same directional source has the same specular response
                    # whether it is fetched as the main or an additional light.
                    local_spec = light_response(data, light, _WaveFoamLightScale=0, _WaveFoamSpecularBoost=1)
                    sun = shading.sun_highlight(0, variance=variance, directions=(normal, UP, light))
                    self.assertVector(local_spec, (sun, sun, sun))

    def test_local_foam_aa_uses_relief_variance_independently_of_water(self):
        for variance in (0, .0001, .001, .02):
            data = phase_data(UP, variance, 1)
            expected = reference_light(UP, variance, UP, _WaveFoamLightScale=0)
            for water_variance in (0, .002, .05):
                self.assertVector(light_response(data, UP, water_variance=water_variance, _WaveFoamLightScale=0), expected)

    def test_two_phase_lighting_matches_covered_area_composition(self):
        normals = (normalize((.8, 1, -.3)), normalize((-.6, 1, .2)))
        variances = (.001, .02)
        light, view = normalize((-.4, 1, .1)), normalize((.2, 1, -.3))
        phase_colours = [reference_light(n, v, light, view) for n, v in zip(normals, variances)]
        for alpha0 in (0, .01, .2, 1):
            for alpha1 in (0, .03, .7, 1):
                first, second = [phase_data(n, v, a) for n, v, a in zip(normals, variances, (alpha0, alpha1))]
                for weight in (0, .1, .5, .9, 1):
                    data = flow_data(first, second, alpha0, alpha1, weight)
                    coverage = weight * alpha0 + (1 - weight) * alpha1
                    expected = phase_colours[0] * (weight * alpha0) + phase_colours[1] * ((1 - weight) * alpha1)
                    actual = light_response(data, light, view, flow=True) * coverage
                    self.assertVector(actual, expected)

    def test_phase_normals_are_lit_separately_without_inventing_a_mean_highlight(self):
        first, second = normalize((.5, math.sqrt(.75), 0)), normalize((-.5, math.sqrt(.75), 0))
        data = flow_data(phase_data(first, 0, 1), phase_data(second, 0, 1), 1, 1, .5)
        actual = light_response(data, UP, flow=True, _WaveFoamLightScale=0, _WaveFoamSpecularBoost=1)
        # Each actual reflection has cosine 1/2; an averaged normal would create
        # an artificial unit peak, roughly 10^88 times either phase's highlight.
        expected = .5 ** 293 * math.sqrt(.75)
        self.assertLess(max(actual), 1e-80)
        self.assertVector(actual, (expected,) * 3, tolerance=1e-95)

    def test_empty_phase_cannot_lend_normal_variance_or_highlight(self):
        covered_normal = normalize((.3, 1, -.4))
        expected = reference_light(covered_normal, .01, UP)
        covered = phase_data(covered_normal, .01, .7)
        for empty_normal in (UP, normalize((-1, .01, 0)), normalize((1, .1, -.7))):
            empty = phase_data(empty_normal, 0, 0)
            for weight in (.01, .25, .5, .99):
                for first, second, a0, a1 in ((empty, covered, 0, .7), (covered, empty, .7, 0)):
                    actual = light_response(flow_data(first, second, a0, a1, weight), UP, flow=True)
                    self.assertVector(actual, expected)
        self.assertVector(light_response(phase_data(UP, 0, 0), UP), ZERO)

    def test_flow_endpoints_and_phase_reset_remain_continuous(self):
        def phase(offset):
            return normalize((.5 * math.sin(2 * offset), 1, .3 * math.cos(offset))), .005 + .004 * math.sin(offset), .5 + .3 * math.cos(2 * offset)

        def sample(time):
            offset0, offset1, weight = flow_timing(time)
            n0, v0, a0 = phase(offset0)
            n1, v1, a1 = phase(offset1)
            data = flow_data(phase_data(n0, v0, a0), phase_data(n1, v1, a1), a0, a1, weight)
            actual = light_response(data, UP, flow=True)
            coverage = weight * a0 + (1 - weight) * a1
            if weight in (0, 1):
                self.assertVector(actual, reference_light(n0, v0, UP) if weight else reference_light(n1, v1, UP))
            return actual * coverage

        for reset in (1, 2, 3, 10):
            sample(reset)
            self.assertVector(sample(reset - 1e-7), sample(reset + 1e-7), tolerance=2e-6)

    def test_light_hemisphere_attenuation_and_cookie_colour_apply_to_each_phase(self):
        normal = normalize((1, 1, .2))
        for light in (-normal, normalize((-1, .01, 0)), UP):
            for colour in (Vector((1, 1, 1)), Vector((0, .2, .7)), ZERO):
                for shadow in (0, .3, 1):
                    for distance in (0, .2, 1):
                        data = phase_data(normal, .005, 1)
                        actual = light_response(data, light, colour=colour, shadow=shadow, distance=distance)
                        self.assertVector(actual, reference_light(normal, .005, light, colour=colour, shadow=shadow, distance=distance))

    def test_flat_and_disabled_foam_keep_their_existing_local_light_response(self):
        water, light = normalize((.3, 1, .6)), normalize((-.1, .4, 1))
        data = flow_data(phase_data(UP, .01, 1), phase_data(-UP, 0, 1), 1, 1, .5)
        expected = DEFAULTS["_FoamWhiteColor"].rgb * DEFAULTS["_WaveFoamLightScale"] * max(0, dot(water, light))
        for flow in (False, True):
            self.assertVector(light_response(data, light, water=water, relief=False, flow=flow), expected)
            self.assertVector(light_response(data, light, water=water, foam=False, flow=flow), ZERO)

    def test_tiny_phase_coverage_uses_float_weights_and_the_same_colour_normalisation(self):
        # The input alpha is already half storage in ComputeFoam. Reuse its
        # representable values; only the weight products/sum need float storage.
        normals = (normalize((.3, 1, -.5)), UP)
        colours = [reference_light(n, .01, UP) for n in normals]
        for alpha in (0, 2 ** -24, 2 ** -14, .001, 1):
            alpha = shading.quantize(alpha, "e")
            for weight in (0, 1e-6, .001, .5, 1):
                first, second = phase_data(normals[0], .01, alpha), phase_data(normals[1], .01, 0)
                data = flow_data(first, second, alpha, 0, weight, binary32=True)
                actual = light_response(data, UP, flow=True)
                blend = shading.flow_blend(Vector((*colours[0], alpha)), Vector((*colours[1], 0)), weight)
                self.assertTrue(all(math.isfinite(channel) and channel >= 0 for channel in actual))
                self.assertVector(actual, blend.rgb, tolerance=5e-7)

    def test_water_lighting_keeps_its_water_normal_and_variance(self):
        body = accumulator(True, True, True)
        water, view, light = normalize((-.3, 1, .4)), normalize((.7, 1, -.2)), normalize((-.4, 1, .1))
        values = {**DEFAULTS, "normalWS": water, "view": view, "light.direction": light, "normalVariance": .003,
                  "radiance": Vector((.2, .3, .4)), "scatterTint": Vector((.1, .2, .3))}
        values["ndl"] = equation(shader.assignment(body, "ndl"), values)
        scatter = equation(re.search(r"\bscatter\s*\+=\s*([^;]+);", body)[1], values)
        specular = equation(re.search(r"\bspecular\s*\+=\s*([^;]+);", body)[1], values)
        expected_scatter = values["scatterTint"] * values["radiance"] * max(0, dot(water, light))
        expected_specular = reference_light(water, .003, light, view, colour=values["radiance"],
            _WaveFoamLightScale=0, _WaveFoamSpecularBoost=DEFAULTS["_AdditionalLightBoost"],
            _WaveFoamSpecularFallOff=DEFAULTS["_AdditionalLightFallOff"])
        self.assertVector(scatter, expected_scatter)
        self.assertVector(specular, expected_specular)

    def test_light_fetch_layer_gate_and_phase_keyword_cost_are_preserved(self):
        lights = shader.source(LIGHTS)
        body = shader.function_body(LIGHTS, "CrestAdditionalLights")
        fetches = calls(body, "GetAdditionalLight")
        accumulations = calls(body, "CrestAccumulateAdditionalLight")
        self.assertEqual(len(fetches), 2)  # One clustered directional loop and one normal light loop.
        self.assertEqual(len(accumulations), 2)
        for fetch in fetches:
            self.assertEqual(fetch, ["lightIndex", "positionWS", "half4(1, 1, 1, 1)"])
        self.assertIn("LIGHT_LOOP_BEGIN(lightCount)", body)
        self.assertIn("CLUSTER_LIGHT_LOOP_SUBTRACTIVE_LIGHT_CHECK", body)
        accumulate = shader.function_body(LIGHTS, "CrestAccumulateAdditionalLight")
        self.assertRegex(accumulate, r"#if defined\(_LIGHT_LAYERS\)\s+if \(!IsMatchingLightLayer\(light.layerMask, GetMeshRenderingLayer\(\)\)\) return;")
        self.assertLess(accumulate.index("IsMatchingLightLayer"), accumulate.index("radiance ="))
        self.assertNotRegex(lights, r"\b(?:ddx|ddy|fwidth|Sample|SampleLevel|SampleGrad|CrestSpecularNormalVariance)\s*\(")
        for foam, relief, flow, expected in ((True, True, False, 1), (True, True, True, 2),
                                            (True, False, True, 0), (False, True, True, 0)):
            self.assertEqual(len(calls(accumulator(foam, relief, flow), "CrestFoamAdditionalLight")), expected)

    def test_foam_data_reaches_existing_light_loops_without_extra_texture_samples(self):
        source = shader.source(FOAM)
        self.assertTrue("struct CrestFoamLightingData" in source, "Foam relief is discarded before local lights")
        self.assertLess(source.index("struct CrestFoamLightingData"), source.index("#if _FOAM_ON"))
        compute, flow = shader.function_body(FOAM, "ComputeFoam"), shader.function_body(FOAM, "ComputeFoamWithFlow")
        self.assertEqual(compute.count("WhiteFoamTexture("), 3)
        self.assertEqual(len(calls(flow, "ComputeFoam")), 2)
        self.assertEqual(shader.function_body(FOAM, "WhiteFoamTexture").count("i_texture.Sample("), 2)
        # The derivative is still evaluated unconditionally, before any coverage
        # guard; the exported values are exactly those used by the main light.
        self.assertLess(compute.index("CrestSpecularNormalVariance(fN)"), compute.index("o_foamLighting.weights"))
        ocean = shader.source(shader.regression.OCEAN)
        self.assertIn("CrestFoamLightingData foamLighting = (CrestFoamLightingData)0", ocean)
        for name in ("ComputeFoam", "ComputeFoamWithFlow"):
            arguments = calls(ocean, name)[0]
            self.assertEqual(arguments[arguments.index("whiteFoamCol") + 1], "foamLighting")
        arguments = calls(ocean, "CrestAdditionalLights")[0]
        self.assertEqual(arguments[arguments.index("normalVariance") + 1], "foamLighting")
        for arguments in calls(shader.function_body(LIGHTS, "CrestAdditionalLights"), "CrestAccumulateAdditionalLight"):
            self.assertEqual(arguments[arguments.index("normalVariance") + 1], "foamLighting")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read production source from this Git revision")
    args, remaining = parser.parse_known_args()
    shader.regression.SOURCE_REF = args.ref
    print("Foam lighting: source equations/data flow, covered-area light invariants and float weight storage; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
