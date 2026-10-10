#!/usr/bin/env python3
"""URP source and numerical regressions. No Unity import or GPU execution.

Use --ref <commit> to demonstrate that a previous source revision fails these checks.
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
CREST = "crest/Assets/Crest/Crest/"
EXAMPLES = "crest/Assets/Crest/Crest-Examples/"
CORE = CREST + "Shaders/Helpers/URP/Core.hlsl"
FEATURE = CREST + "Scripts/URP/CrestUniversalRendererFeature.cs"
OCEAN = CREST + "Shaders/Ocean.shader"
MASKED = EXAMPLES + "Examples/Shaders/Masked.shader"
TRANSPARENT = EXAMPLES + "Shared/Shaders/ExampleUnderwaterTransparentSurfaceShader.shader"
SHADOW_SHADERS = (
    OCEAN, MASKED, TRANSPARENT,
    CREST + "Shaders/Resources/UpdateShadow.shader",
    CREST + "Shaders/Underwater/Resources/UnderwaterEffect.shader",
    CREST + "Shaders/Underwater/UnderwaterCurtain.shader",
)
RECONSTRUCTION = (
    CREST + "Shaders/OceanEmission.hlsl",
    CREST + "Shaders/Underwater/UnderwaterEffect.hlsl",
    CREST + "Shaders/Underwater/UnderwaterCurtain.shader",
)
SOURCE_REF = None


def source(path):
    if SOURCE_REF:
        text = subprocess.check_output(["git", "show", f"{SOURCE_REF}:{path}"], cwd=ROOT).decode("utf-8-sig")
    else:
        text = (ROOT / path).read_text(encoding="utf-8-sig")
    return re.sub(r"/\*.*?\*/|//[^\n]*", "", text, flags=re.S)


def body(text, name):
    start = re.search(r"\b" + name + r"\s*\([^{}]*\)\s*\{", text).end()
    depth = 1
    for end in range(start, len(text)):
        depth += (text[end] == "{") - (text[end] == "}")
        if depth == 0:
            return text[start:end]
    raise AssertionError(f"Unclosed function {name}")


def conditional(text, keywords):
    """Select the simple defined() branches in the atlas-coordinate helper."""
    enabled, taken, lines = True, False, []
    for line in text.splitlines():
        match = re.match(r"\s*#(if|elif)\s+defined\((\w+)\)", line)
        if match:
            enabled = match[2] in keywords and (match[1] == "if" or not taken)
            taken = enabled if match[1] == "if" else taken or enabled
        elif line.strip() == "#endif":
            enabled, taken = True, False
        elif enabled:
            lines.append(line)
    return "\n".join(lines)


class Vec(tuple):
    def __add__(self, other):
        return Vec(a + b for a, b in zip(self, other))

    def __sub__(self, other):
        return Vec(a - b for a, b in zip(self, other))

    def __mul__(self, scale):
        return Vec(a * scale for a in self)

    __rmul__ = __mul__

    def __truediv__(self, scale):
        return self * (1.0 / scale)

    def __neg__(self):
        return self * -1

    @property
    def xyz(self):
        return Vec(self[:3])


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def float4(vector, w):
    return Vec((*vector, w))


def mul(matrix, vector):
    return Vec(dot(row, vector) for row in matrix)


def evaluate(expression, values):
    """Evaluate the actual source expression, allowing only vector arithmetic."""
    parsed = ast.parse(expression, mode="eval")
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Call, ast.Name, ast.Load,
               ast.Constant, ast.Add, ast.Sub, ast.Mult, ast.Div, ast.USub,
               ast.Subscript, ast.Attribute, ast.Compare, ast.Gt)
    functions = {"dot": dot, "mul": mul, "float4": float4}
    for node in ast.walk(parsed):
        if not isinstance(node, allowed):
            raise AssertionError(f"Unexpected shader expression: {expression}")
        if isinstance(node, ast.Call) and (not isinstance(node.func, ast.Name) or node.func.id not in functions):
            raise AssertionError("Unexpected expression call")
        if isinstance(node, ast.Attribute) and node.attr != "xyz":
            raise AssertionError("Unexpected swizzle")
    return eval(compile(parsed, "<shader-expression>", "eval"), {"__builtins__": {}}, dict(functions, **values))


def rotate(vector, angles):
    x, y, z = vector
    for axis, degrees in enumerate(angles):
        c, s = math.cos(math.radians(degrees)), math.sin(math.radians(degrees))
        if axis == 0:
            y, z = c * y - s * z, s * y + c * z
        elif axis == 1:
            x, z = c * x + s * z, -s * x + c * z
        else:
            x, y = c * x - s * y, s * x + c * y
    return Vec((x, y, z))


class URPRegressions(unittest.TestCase):
    def assertVectorClose(self, actual, expected, label):
        self.assertEqual(len(actual), len(expected))
        for a, b in zip(actual, expected):
            self.assertAlmostEqual(a, b, delta=1e-7, msg=label)

    def test_camera_matrix_conventions(self):
        self.assertNotRegex(source(CORE), r"#define\s+unity_CameraToWorld\b")
        self.assertRegex(source(RECONSTRUCTION[0]), r"#if defined\(CREST_URP\)\s+float3 scenePos = .*UNITY_MATRIX_I_V")

    def test_reconstructed_points_are_in_front_with_correct_eye_depth(self):
        # Expected points come from camera-local coordinates, independently of the shader formula.
        for path in RECONSTRUCTION:
            expression = re.search(r"float3 scenePos\s*=\s*([^;]+);", source(path))[1]
            expression = expression.replace("UNITY_MATRIX_I_V._13_23_33", "backward")
            alias = bool(re.search(r"#define\s+unity_CameraToWorld\s+UNITY_MATRIX_I_V", source(CORE)))
            expression = expression.replace("unity_CameraToWorld._m02_m12_m22", "backward" if alias else "forward")
            for camera in (Vec((0, 0, 0)), Vec((40, 12, -120)), Vec((80000, -2000, -40000))):
                for angles in ((0, 0, 0), (23, 67, -14), (-42, -121, 31)):
                    right, up, forward = (rotate(v, angles) for v in ((1, 0, 0), (0, 1, 0), (0, 0, 1)))
                    for sx, sy in ((0, 0), (.35, -.4), (1.6, .8)):
                        for depth in (.4, 25, 500):
                            expected = camera + (forward + right * sx + up * sy) * depth
                            view = (camera - expected) / math.sqrt(dot(camera - expected, camera - expected))
                            actual = evaluate(expression, {"_WorldSpaceCameraPos": camera, "i_view": view, "view": view,
                                                           "sceneZ": depth, "backward": -forward, "forward": forward})
                            self.assertVectorClose(actual, expected, path)
                            self.assertAlmostEqual(dot(actual - camera, forward), depth, delta=1e-7, msg=path)

    def test_msaa_is_recorded_per_camera_before_mask(self):
        feature = source(FEATURE)
        self.assertIn("var descriptor = cameraData.cameraTargetDescriptor;", feature)
        self.assertIn("data.descriptor = descriptor;", feature)
        stage = feature.split("if (pass.stage == 0)", 1)[1]
        self.assertIn("cmd.SetGlobalInt(OceanRenderer.sp_CrestDepthTextureOffset,", stage)
        self.assertLess(stage.index("sp_CrestDepthTextureOffset"), stage.index("RecordURPMask"))
        self.assertNotIn("IsMSAAEnabled", feature)
        self.assertNotRegex(source(CREST + "Scripts/OceanRenderer.cs"), r"Shader.SetGlobalInt\(sp_CrestDepthTextureOffset")

    def test_msaa_fallbacks_and_camera_switches(self):
        match = re.search(r"cmd.SetGlobalInt\(OceanRenderer.sp_CrestDepthTextureOffset,\s*([^;]+)\);", source(FEATURE))
        self.assertIsNotNone(match, "Waterline MSAA must use the rendered camera descriptor")
        condition, results = match[1].split("?")
        yes, no = (int(value.strip()) for value in results.split(":"))
        condition = condition.strip().replace("pass.descriptor.msaaSamples", "samples")
        # Asset 4x / camera disabled or Deferred, target RT 1x/2x, device fallback,
        # and successive cameras with different descriptors. QualitySettings is unrelated.
        for samples, quality, expected in ((1, 8, 0), (4, 0, 1), (1, 4, 0), (2, 0, 1), (8, 0, 1), (1, 8, 0)):
            value = yes if evaluate(condition, {"samples": samples, "quality": quality}) else no
            self.assertEqual(value, expected, (samples, quality))

    def test_all_main_and_soft_shadow_variants(self):
        main = {"_", "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE", "_MAIN_LIGHT_SHADOWS_SCREEN"}
        soft = {"_", "_SHADOWS_SOFT", "_SHADOWS_SOFT_LOW", "_SHADOWS_SOFT_MEDIUM", "_SHADOWS_SOFT_HIGH"}
        for path in SHADOW_SHADERS:
            groups = re.findall(r"#pragma multi_compile(?:_fragment)?\s+([^\n]+)", source(path))
            self.assertIn(main, [set(group.split()) for group in groups], path)
            self.assertIn(soft, [set(group.split()) for group in groups], path)

    def test_world_shadow_queries_use_declared_atlas(self):
        core, feature = source(CORE), source(FEATURE)
        sample = body(core, "CrestMainLightShadow")
        self.assertIn("#if defined(MAIN_LIGHT_CALCULATE_SHADOWS)", sample)
        self.assertRegex(sample, r"#else\s+return 1.0h;")
        self.assertRegex(sample, r"SampleShadowmap\(TEXTURE2D_ARGS\(_MainLightShadowmapTexture, sampler_LinearClampCompare\),\s*CrestWorldToMainLightShadowCoord\(positionWS\), GetMainLightShadowSamplingData\(\), GetMainLightShadowParams\(\), false\)")
        self.assertIn("CrestMainLightShadow(i_scenePos)", source(RECONSTRUCTION[0]))
        self.assertIn("CrestMainLightShadow(positionWS.xyz)", source(SHADOW_SHADERS[3]))
        self.assertIn("data.shadowCascadeCount = shadowData.mainLightShadowCascadesCount;", feature)
        self.assertIn("cmd.SetGlobalInt(s_MainLightShadowCascadeCount, pass.shadowCascadeCount);", feature)
        self.assertIn("builder.UseTexture(resources.mainShadowsTexture)", feature)

    def test_single_cascade_screen_and_atlas_coordinates(self):
        helper = body(source(CORE), "CrestWorldToMainLightShadowCoord")
        # Distinct atlas transforms make selecting a wrong cascade or screen matrix observable.
        matrices = [((.02, 0, 0, i * .15), (0, .03, 0, .1), (0, 0, .01, .2), (0, 0, 0, 1)) for i in range(5)]
        for point in (Vec((2, -3, z)) for z in (5, 12, 30, 50, 80)):
            for keyword, count in (("_MAIN_LIGHT_SHADOWS", 1), ("_MAIN_LIGHT_SHADOWS", 4),
                                   ("_MAIN_LIGHT_SHADOWS_SCREEN", 1), ("_MAIN_LIGHT_SHADOWS_SCREEN", 2),
                                   ("_MAIN_LIGHT_SHADOWS_SCREEN", 3), ("_MAIN_LIGHT_SHADOWS_SCREEN", 4),
                                   ("_MAIN_LIGHT_SHADOWS_CASCADE", 4), ("_MAIN_LIGHT_SHADOWS_CASCADE", 0)):
                # A cascade keyword must work even before the Crest count is initialized (e.g. a render request).
                cascades = 4 if keyword == "_MAIN_LIGHT_SHADOWS_CASCADE" else count
                # URP's split-sphere selection: the first containing sphere, or the no-shadow sentinel 4.
                select = next((i for i, radius in enumerate((8, 20, 40, 60)[:cascades])
                               if dot(point, point) < radius ** 2), 4)
                active = conditional(helper, {keyword})
                index = 0
                if "cascadeIndex = ComputeCascadeIndex(positionWS);" in active:
                    condition = re.search(r"\bif\s*\(([^)]+)\)", active)
                    if condition is None or evaluate(condition[1], {"_CrestMainLightShadowCascadeCount": count}):
                        index = select
                expression = re.search(r"return\s+([^;]+);", active)[1]
                actual = evaluate(expression, {"_MainLightWorldToShadow": matrices, "cascadeIndex": index, "positionWS": point})
                expected_index = select if keyword == "_MAIN_LIGHT_SHADOWS_CASCADE" or (keyword == "_MAIN_LIGHT_SHADOWS_SCREEN" and count > 1) else 0
                expected = float4(mul(matrices[expected_index], float4(point, 1)).xyz, 0)
                self.assertVectorClose(actual, expected, (keyword, count, point))

    def test_surface_shadow_coordinate_routes(self):
        opaque = source(MASKED)
        self.assertRegex(opaque, r"#if defined\(_MAIN_LIGHT_SHADOWS_SCREEN\)\s+data.shadowCoord = i.screenPos;\s+#else\s+data.shadowCoord = TransformWorldToShadowCoord\(i.positionWS\);")
        self.assertIn("o.screenPos = ComputeScreenPos(o.positionCS);", opaque)
        transparent = source(TRANSPARENT)
        self.assertLess(transparent.index("#define _SURFACE_TYPE_TRANSPARENT"), transparent.index('#include "../../../Crest/Shaders/Helpers/URP/Core.hlsl"'))
        self.assertIn("data.shadowCoord = CrestWorldToMainLightShadowCoord(i.positionWS);", transparent)

    def test_reflection_probe_blending_receives_surface_position(self):
        reflection = source(CREST + "Shaders/OceanReflection.hlsl")
        self.assertIn("in const float3 i_positionWS,", reflection)
        self.assertIn("GlossyEnvironmentReflection(refl, i_positionWS, _Roughness, 1.0h, i_screenPos.xy / i_screenPos.w)", reflection)
        self.assertIn("ApplyReflectionSky(view, n_pixel, input.worldPos,", source(OCEAN))
        for path in (OCEAN, MASKED, TRANSPARENT):
            self.assertIn("#pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING", source(path))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read sources from this Git revision instead of the working tree")
    args, remaining = parser.parse_known_args()
    SOURCE_REF = args.ref
    print("URP regressions: source contracts and numerical expressions only; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
