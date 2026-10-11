#!/usr/bin/env python3
"""Source-driven reflection invariants; no Unity execution or GPU validation.

Evaluate the actual HLSL equations against coverage composition, Snell's law,
precision and sampling bounds. Check the corresponding render-texture lifecycle
at source level. Use --ref <commit> to check an earlier implementation.
"""
import argparse
import ast
from functools import lru_cache
import math
import re
import struct
import sys
import unittest

import check_urp_regressions as regression
from check_optical_quality import Vector, dot, maximum, minimum, saturate, smoothstep

REFLECTION = regression.CREST + "Shaders/OceanReflection.hlsl"
SPECULAR = regression.CREST + "Shaders/OceanSpecular.hlsl"
CAMERA = regression.CREST + "Scripts/Reflection/OceanPlanarReflection.cs"


@lru_cache(maxsize=None)
def source(path):
    return regression.source(path)


@lru_cache(maxsize=None)
def function_body(path, name):
    # Anchor the declaration, so an earlier call followed by an unrelated if
    # block cannot be mistaken for this function's definition.
    declaration = re.search(r"(?m)^\s*(?:(?:public|private|protected|internal|static)\s+)*(?:void|float[234]?|half[234]?)\s+"
                            + name + r"\s*\([^{}]*\)\s*\{", source(path))
    if declaration is None:
        raise AssertionError(f"Missing shader helper: {name}")
    return regression.body(source(path)[declaration.start():], name)


def python_ternary(text):
    """Translate the small parenthesized HLSL ternary expressions used here."""
    result, index = "", 0
    while index < len(text):
        if text[index] != "(":
            result += text[index]
            index += 1
            continue
        end, depth = index + 1, 1
        while depth:
            depth += (text[end] == "(") - (text[end] == ")")
            end += 1
        result += "(" + python_ternary(text[index + 1:end - 1]) + ")"
        index = end
    depth, question, colon = 0, None, None
    for index, character in enumerate(result):
        depth += (character == "(") - (character == ")")
        if depth == 0 and character == "?": question = index
        if depth == 0 and character == ":": colon = index
    if question is not None:
        if colon is None: raise AssertionError("Unmatched HLSL conditional")
        return f"{result[question + 1:colon]} if {result[:question]} else {result[colon + 1:]}"
    return result


@lru_cache(maxsize=None)
def compiled_expression(text):
    tree = ast.parse(python_ternary(re.sub(r"\s+", " ", text).strip()), mode="eval")
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Call, ast.Name, ast.Load,
               ast.Constant, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.USub,
               ast.Attribute, ast.Compare, ast.Lt, ast.LtE, ast.Gt, ast.GtE, ast.IfExp)
    for node in ast.walk(tree):
        if not isinstance(node, allowed): raise AssertionError(f"Unsupported HLSL expression: {text}")
        if isinstance(node, ast.Call) and (not isinstance(node.func, ast.Name) or node.func.id not in FUNCTIONS):
            raise AssertionError(f"Unsupported HLSL call: {text}")
        if isinstance(node, ast.Attribute) and node.attr not in ("x", "y", "z", "w", "a", "xy", "xz", "rgb"):
            raise AssertionError(f"Unsupported HLSL swizzle: {text}")
    return compile(tree, "<reflection-shader-equation>", "eval")


def evaluate(text, values):
    return eval(compiled_expression(text), {"__builtins__": {}}, {**FUNCTIONS, **values})


DEFAULTS = {"_RefractiveIndexOfAir": 1.0, "_RefractiveIndexOfWater": 1.333, "_FresnelPower": 5.0,
            "_PlanarReflectionRoughnessScale": 1.0, "_PlanarReflectionEdgeFade": .02,
            "_ScaledScreenParams": Vector((1920, 1080, 0, 0))}


def run_assignments(body, values):
    # Both the previous and current underwater function contain only assignments
    # and lexical blocks. Deliberately reject control flow instead of simulating it.
    values = {**DEFAULTS, **values}
    for statement in body.replace("{", "").replace("}", "").split(";"):
        statement = statement.strip()
        if not statement: continue
        if statement.startswith("return "): return evaluate(statement[7:], values)
        match = re.fullmatch(r"(?:(?:const\s+)?(?:float|half)[234]?\s+)?(\w+)\s*(=|\*=|\+=)\s*(.+)", statement, re.S)
        if not match: raise AssertionError(f"Unsupported HLSL statement: {statement}")
        name, operation, expression = match.groups()
        value = evaluate(expression, values)
        values[name] = value if operation == "=" else values[name] * value if operation == "*=" else values[name] + value
    return values


def shader_function(path, name, *arguments, **globals):
    signature = re.search(r"\b" + name + r"\s*\(([^)]*)\)", source(path))
    if signature is None: raise AssertionError(f"Missing shader helper: {name}")
    names = [parameter.strip().split()[-1] for parameter in signature[1].split(",")]
    return run_assignments(function_body(path, name), {**globals, **dict(zip(names, arguments))})


FUNCTIONS = {
    "max": maximum, "min": minimum, "saturate": saturate, "sqrt": math.sqrt,
    "log2": math.log2, "exp2": lambda x: 2 ** x, "pow": pow, "dot": dot,
    "clamp": lambda x, low, high: minimum(maximum(x, low), high), "smoothstep": smoothstep,
    "sin": math.sin, "cos": math.cos, "asin": math.asin, "acos": math.acos,
    "lerp": lambda a, b, t: a * (1 - t) + b * t,
    "CalculateFresnelReflectionCoefficient": lambda x: shader_function(REFLECTION, "CalculateFresnelReflectionCoefficient", x),
}


def mip(size, roughness, scale=1, screen=(1920, 1080), count=None):
    count = 1 + math.floor(math.log2(max(size))) if count is None else count
    return shader_function(REFLECTION, "CrestPlanarReflectionMip", Vector(size), count, roughness,
                           _PlanarReflectionRoughnessScale=scale, _ScaledScreenParams=Vector(screen))


def edge(uv, size=(256, 256), level=0, fade=.02):
    return shader_function(REFLECTION, "CrestPlanarReflectionEdgeWeight", Vector(uv), Vector(size), level,
                           _PlanarReflectionEdgeFade=fade)


def planar_body():
    body = function_body(REFLECTION, "PlanarReflection")
    return body.split("#if defined(CREST_URP)", 1)[-1].split("#else", 1)[0]


def assignment(body, name):
    match = re.search(r"\b" + name + r"\s*=\s*([^;]+);", body)
    if match is None: raise AssertionError(f"Missing shader assignment: {name}")
    return match[1]


def compose_planar(background, sampled, intensity=1, edge_weight=1):
    body = planar_body()
    values = {"io_colour": background, "refl": sampled,
              "_PlanarReflectionIntensity": intensity, "edgeWeight": edge_weight}
    for name in ("coverage", "intensity"):
        if re.search(r"\b" + name + r"\s*=", body): values[name] = evaluate(assignment(body, name), values)
    guard = re.search(r"\bif\s*\(([^()]*)\)\s*\{\s*io_colour", body)
    if guard and not evaluate(guard[1], values): return background
    return evaluate(assignment(body, "io_colour"), values)


def underwater(cosine, weight=1, exterior=(1, 1, 1), interior=(1, 1, 1)):
    return run_assignments(function_body(REFLECTION, "ApplyReflectionUnderwater"), {
        "i_n_pixel": Vector((0, 1, 0)), "i_view": Vector((0, cosine, 0)), "i_weight": weight,
        "io_col": Vector(exterior), "scatterCol": Vector(interior),
    })["io_col"]


class ReflectionQuality(unittest.TestCase):
    def assertVectorClose(self, actual, expected, tolerance=1e-10):
        for actual_channel, expected_channel in zip(actual, expected):
            self.assertAlmostEqual(actual_channel, expected_channel, delta=tolerance)

    def test_mip_levels_are_bounded_and_monotone_in_roughness(self):
        for size in ((1, 1), (3, 3), (128, 128), (256, 256), (1024, 512), (4096, 4096)):
            for scale in (0, .25, 1, 2):
                previous = -1
                for roughness in (-1, 0, .05, .2, .4, .6, .8, 1, 2):
                    actual = mip(size, roughness, scale)
                    self.assertTrue(math.isfinite(actual))
                    self.assertGreaterEqual(actual, previous)
                    self.assertGreaterEqual(actual, 0)
                    self.assertLessEqual(actual, math.floor(math.log2(max(size))))
                    previous = actual
            # No chain, including Core.GetMipCount's unsupported-backend result.
            for count in (0, 1): self.assertEqual(mip(size, 1, count=count), 0)

    def test_roughness_footprint_is_resolution_stable_and_controls_are_effective(self):
        for roughness in (.4, .6, .8, 1):
            footprints = [2 ** mip((size, size), roughness) / size for size in (128, 256, 512, 1024)]
            self.assertLess(max(footprints) - min(footprints), 1e-12)
        self.assertEqual(mip((256, 256), 1, scale=0), 0)
        self.assertLess(mip((256, 256), .7, scale=.5), mip((256, 256), .7, scale=1))
        # A reflection larger than the target still needs minification filtering
        # when authored roughness is zero.
        self.assertEqual(mip((2048, 2048), 0, screen=(512, 512)), 2)

    def test_existing_normal_variance_reaches_planar_filter_without_new_derivatives(self):
        body = planar_body()
        self.assertIn("CrestFilteredPerceptualRoughness(_Roughness, i_normalVariance)", body)
        self.assertNotRegex(body, r"\b(?:ddx|ddy|fwidth)\s*\(")
        self.assertIn("PlanarReflection(i_screenPos, i_n_pixel, i_pixelZ, i_normalVariance, skyColour)", source(REFLECTION))
        previous = -1
        for variance in (0, .0001, .001, .01, .05):
            roughness = shader_function(SPECULAR, "CrestFilteredPerceptualRoughness", .2, variance)
            actual = mip((256, 256), roughness)
            self.assertGreaterEqual(actual, previous)
            previous = actual

    def test_screen_edge_weight_is_bounded_continuous_and_rejects_outside_capture(self):
        for level in (0, 1.5, 4):
            for fade in (0, .02, .25):
                for uv in ((-.2, .5), (1.2, .5), (.5, -.2), (.5, 1.2), (0, .5), (1, .5)):
                    self.assertEqual(edge(uv, level=level, fade=fade), 0)
                previous = 0
                for index in range(101):
                    value = edge((index / 200, .5), level=level, fade=fade)
                    self.assertTrue(math.isfinite(value))
                    self.assertGreaterEqual(value, previous)
                    self.assertLessEqual(value, 1)
                    previous = value
                self.assertEqual(previous, 1)
                self.assertLess(edge((1e-9, .5), level=level, fade=fade), 1e-10)
        # With optional fade zero, retain a half-filtered-texel safety band.
        self.assertGreater(edge((.01, .5), level=0, fade=0), edge((.01, .5), level=4, fade=0))

    def test_filtered_coverage_matches_independent_area_average(self):
        background, covered = Vector((.1, .3, .6)), Vector((2, 1, .5))
        for coverage in (0, .01, .125, .5, .9, 1):
            sample = Vector((*tuple(covered * coverage), coverage))
            for intensity in (0, .25, 1):
                for edge_weight in (1, .3, 0):
                    expected = background * (1 - coverage * intensity * edge_weight) + covered * (coverage * intensity * edge_weight)
                    self.assertVectorClose(compose_planar(background, sample, intensity, edge_weight), expected)

    def test_zero_or_negative_alpha_never_contributes_rgb(self):
        background = Vector((.2, .4, .8))
        for alpha in (-2, -.01, 0):
            self.assertVectorClose(compose_planar(background, Vector((100, 40, 20, alpha))), background)

    def test_homogeneous_screen_precision_survives_far_water(self):
        body = planar_body()
        precision = re.search(r"\b(float4|half4)\s+screenPos\s*=", body)[1]
        def quantize(value, kind):
            try: return struct.unpack(kind, struct.pack(kind, float(value)))[0]
            except OverflowError: return math.copysign(math.inf, value)

        witnessed_half_loss = False
        for w in (1000, 80000, 1000000):
            for expected in (.127314, .372519, .892631):
                xy = quantize(expected * w, "f")
                actual = quantize(xy, "f" if precision == "float4" else "e") / quantize(w, "f" if precision == "float4" else "e")
                self.assertTrue(math.isfinite(actual))
                self.assertLess(abs(actual - expected) * 3840, 1 / 32)
                old = quantize(xy, "e") / quantize(w, "e")
                witnessed_half_loss |= not math.isfinite(old) or abs(old - expected) * 3840 > .25
        self.assertTrue(witnessed_half_loss)
        for name in ("PlanarReflection", "ApplyReflectionSky"):
            signature = re.search(r"\b" + name + r"\s*\(([^{}]*)\)\s*\{", source(REFLECTION))[1]
            self.assertIn("float4 i_screenPos", signature)

    def test_underwater_equal_inputs_keep_constant_color(self):
        for cosine in (0, .1, .5, .66, .75, .9, 1):
            for weight in (0, .2, .5, 1):
                actual = underwater(cosine, weight)
                self.assertTrue(all(math.isfinite(channel) for channel in actual))
                self.assertVectorClose(actual, (1, 1, 1))

    def test_underwater_dot_roundoff_remains_finite(self):
        for cosine in (-.001, 1, 1.0009765625):
            try:
                actual = underwater(cosine, exterior=(.7, .2, .1), interior=(.1, .3, .8))
            except ValueError as error:
                self.fail(f"Shader expression leaves its mathematical domain at dot={cosine}: {error}")
            self.assertTrue(all(math.isfinite(channel) and 0 <= channel <= 1 for channel in actual))

    def test_underwater_critical_angle_is_continuous_and_total_internal_reflection_complete(self):
        critical = math.sqrt(1 - (DEFAULTS["_RefractiveIndexOfAir"] / DEFAULTS["_RefractiveIndexOfWater"]) ** 2)
        for cosine in (0, .2, critical - 1e-5, critical):
            actual = underwater(cosine, exterior=(0, 0, 0), interior=(1, 1, 1))
            self.assertVectorClose(actual, (1, 1, 1), tolerance=1e-6)
        above = underwater(critical + 1e-10, exterior=(0, 0, 0), interior=(1, 1, 1))
        self.assertGreater(above[0], .9998)
        # At normal incidence, retain the existing index-based Schlick reflectance.
        normal = underwater(1, exterior=(0, 0, 0), interior=(1, 1, 1))
        expected = ((1 - 1.333) / (1 + 1.333)) ** 2
        self.assertVectorClose(normal, (expected,) * 3)

    def test_mips_are_generated_after_successful_render_before_texture_publication(self):
        late = function_body(CAMERA, "LateUpdate")
        render_try = re.search(r"\btry\s*\{(.*?)\}\s*(?:catch|finally)", late, re.S)
        self.assertIsNotNone(render_try)
        calls = ("CrestRenderRequest.Render(", ".GenerateMips()", "PreparedReflections.Register(")
        for call in calls: self.assertIn(call, render_try[1])
        phases = [render_try[1].index(call) for call in calls]
        self.assertEqual(phases, sorted(phases))
        self.assertRegex(render_try[1], r"if\s*\(_reflectionTexture.useMipMap\)\s*_reflectionTexture.GenerateMips\(\)")
        create = function_body(CAMERA, "CreateWaterObjects")
        self.assertNotIn("PreparedReflections.Register(", create)
        for setting in ("antiAliasing = 1", "useMipMap = _useMipMaps", "autoGenerateMips = false",
                        "filterMode = _useMipMaps ? FilterMode.Trilinear : FilterMode.Bilinear", "wrapMode = TextureWrapMode.Clamp"):
            self.assertIn(setting, create)
        self.assertLess(late.index("!RequestRefresh("), late.index("CreateWaterObjects("))

        # The texture may already be published before its in-place refresh.
        # Neither a render failure nor a mip failure may leave it published.
        recovery = re.search(r"\bcatch\s*\{(.*?)\}\s*finally", late, re.S)
        self.assertIsNotNone(recovery)
        self.assertIn("PreparedReflections.Remove(_camViewpoint.GetHashCode())", recovery[1])
        self.assertIn("throw;", recovery[1])
        self.assertNotIn("PreparedReflections.Register(", late[recovery.end():])

    def test_lost_resources_bypass_frame_throttling(self):
        late = function_body(CAMERA, "LateUpdate")
        condition = re.search(r"\bif\s*\((.*?)\)\s*return\s*;", late, re.S)[1]
        condition = condition.replace("_reflectionTexture.IsCreated()", "created")
        condition = condition.replace("_reflectionTexture", "allocated")
        condition = condition.replace("RequestRefresh(Time.renderedFrameCount)", "refresh")
        condition = condition.replace("&&", " and ").replace("!", " not ").strip()
        tree = ast.parse(condition, mode="eval")
        allowed = (ast.Expression, ast.BoolOp, ast.UnaryOp, ast.And, ast.Not, ast.Name, ast.Load)
        self.assertTrue(all(isinstance(node, allowed) for node in ast.walk(tree)))
        expression = compile(tree, "<reflection-refresh-condition>", "eval")
        for allocated in (False, True):
            for created in (False, True):
                for refresh in (False, True):
                    skip = eval(expression, {"__builtins__": {}}, dict(allocated=allocated, created=created, refresh=refresh))
                    expected = allocated and created and not refresh
                    self.assertEqual(skip, expected)

    def test_texture_replacement_lost_resources_and_cached_lookup_are_handled(self):
        create = function_body(CAMERA, "CreateWaterObjects")
        for condition in ("!_reflectionTexture.IsCreated()", "_textureHDR != _hdr", "_textureStencil != _stencil", "_reflectionTexture.useMipMap != _useMipMaps"):
            self.assertIn(condition, create)
        self.assertIn("if (!_reflectionTexture.Create())", create)
        self.assertIn("PreparedReflections.Remove(currentCamera.GetHashCode())", create)
        register = function_body(CAMERA, "Register")
        self.assertRegex(register, r"if\s*\(_referenceCameraInstanceId == instanceId\)\s*_currentreflectiontexture = reflectionTexture;")
        remove = function_body(CAMERA, "Remove")
        self.assertNotIn("GetRenderTexture(", remove)
        self.assertIn("_collection.Where(e => e.Key != camerainstanceid)", remove)

    def test_urp_queries_follow_core_platform_guard_and_birp_keeps_original_sampling(self):
        body = function_body(REFLECTION, "PlanarReflection")
        self.assertIn("GetMipCount(TEXTURE2D_ARGS(_ReflectionTex, sampler_ReflectionTex))", body)
        self.assertIn("_ReflectionTex.GetDimensions(width, height)", body)
        self.assertNotRegex(body, r"GetDimensions\([^)]*,[^)]*,[^)]*,")
        self.assertIn("SAMPLE_TEXTURE2D_LOD(_ReflectionTex", planar_body())
        self.assertIn("clamp(uv, halfTexel, 1.0 - halfTexel)", planar_body())
        self.assertIn("SAMPLE_TEXTURE2D(_ReflectionTex", body.split("#else", 1)[1])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read production source from this git revision")
    args, remaining = parser.parse_known_args()
    regression.SOURCE_REF = args.ref
    print("Reflection quality: actual HLSL expressions and source lifecycle checks; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
