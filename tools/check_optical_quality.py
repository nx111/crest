#!/usr/bin/env python3
"""Source-driven optical invariants; no Unity compilation or GPU rendering.

The equations are read from the shaders and checked against independent geometry,
Beer-Lambert composition, Snell's law and depth-buffer conventions.
"""
import argparse
import ast
import math
import re
import struct
import sys
import unittest

import check_urp_regressions as regression

EMISSION = regression.CREST + "Shaders/OceanEmission.hlsl"
UNDERWATER = regression.CREST + "Shaders/Underwater/"


class Vector(tuple):
    def operation(self, other, function):
        return Vector(function(a, b) for a, b in zip(self, other if isinstance(other, tuple) else [other] * len(self)))

    def __add__(self, other): return self.operation(other, lambda a, b: a + b)
    def __sub__(self, other): return self.operation(other, lambda a, b: a - b)
    def __mul__(self, other): return self.operation(other, lambda a, b: a * b)
    def __truediv__(self, other): return self.operation(other, lambda a, b: a / b)
    def __radd__(self, other): return self + other
    def __rsub__(self, other): return -self + other
    def __rmul__(self, other): return self * other
    def __neg__(self): return self * -1

    def __getattr__(self, name):
        names = "xyzw" if all(letter in "xyzw" for letter in name) else "rgba"
        values = Vector(self[names.index(letter)] for letter in name)
        return values[0] if len(values) == 1 else values


def componentwise(function, a, b):
    if isinstance(a, tuple): return Vector(a).operation(b, function)
    if isinstance(b, tuple): return Vector(b).operation(a, lambda x, y: function(y, x))
    return function(a, b)


def maximum(a, b): return componentwise(max, a, b)
def minimum(a, b): return componentwise(min, a, b)
def saturate(value): return minimum(maximum(value, 0), 1)
def exp(value): return Vector(math.exp(x) for x in value) if isinstance(value, tuple) else math.exp(value)
def dot(a, b): return sum(x * y for x, y in zip(a, b))
def length(value): return math.sqrt(dot(value, value))


def smoothstep(low, high, value):
    t = saturate((value - low) / (high - low))
    return t * t * (3 - 2 * t)


def evaluate(expression, values, extra_functions=None):
    expression = re.sub(r"\s+", " ", expression).strip()
    expression = expression.replace("UNITY_MATRIX_I_V._13_23_33", "backward").replace("unity_OrthoParams.w", "orthographic")
    expression = expression.replace("(float3x3)", "")
    expression = expression.replace("||", " or ").replace("&&", " and ")
    if "?" in expression:
        condition, choices = expression.split("?", 1)
        yes, no = choices.split(":", 1)
        expression = f"({yes.strip()} if {condition.strip()} else {no.strip()})"
    functions = {"dot": dot, "length": length, "max": maximum, "min": minimum, "sqrt": math.sqrt, "abs": abs,
                 "exp": exp, "saturate": saturate, "smoothstep": smoothstep,
                 "normalize": lambda vector: vector / length(vector),
                 "mul": lambda matrix, vector: Vector(dot(row[:len(vector)], vector) for row in matrix[:len(vector)]),
                 "float3": lambda *args: Vector(args), "clamp": lambda x, lo, hi: minimum(maximum(x, lo), hi), **(extra_functions or {})}
    tree = ast.parse(expression, mode="eval")
    allowed = (ast.Expression, ast.BinOp, ast.UnaryOp, ast.Call, ast.Name, ast.Load, ast.Constant,
               ast.Add, ast.Sub, ast.Mult, ast.Div, ast.USub, ast.Attribute, ast.Compare,
               ast.Lt, ast.LtE, ast.Gt, ast.GtE, ast.IfExp, ast.BoolOp, ast.And, ast.Or)
    for node in ast.walk(tree):
        if not isinstance(node, allowed): raise AssertionError(f"Unsupported equation: {expression}")
        if isinstance(node, ast.Call) and (not isinstance(node.func, ast.Name) or node.func.id not in functions):
            raise AssertionError(f"Unsupported call: {expression}")
        if isinstance(node, ast.Attribute) and node.attr not in ("x", "y", "z", "w", "xy", "xz", "xyz", "rgb"):
            raise AssertionError(f"Unsupported swizzle: {expression}")
    return eval(compile(tree, "<optical-shader-equation>", "eval"), {"__builtins__": {}}, {**functions, **values})


def assignment(source, name):
    match = re.search(r"\b" + re.escape(name) + r"\s*=\s*([^;]+);", source)
    if not match: raise AssertionError(f"Missing shader equation: {name}")
    return match[1]


class OpticalQuality(unittest.TestCase):
    def test_ray_attenuation_matches_world_length_under_camera_rotation(self):
        helper = regression.body(regression.source(regression.HELPERS), "CrestWaterRayDistance")
        alignment, distance = assignment(helper, "alignment"), re.search(r"return\s+([^;]+);", helper)[1]
        # alpha's declaration is zero; select the actual Beer-Lambert assignment.
        opacity = next(value for value in re.findall(r"\balpha\s*=\s*([^;]+);", regression.source(EMISSION)) if "exp(" in value)
        for angles in ((0, 0, 0), (21, -67, 18), (-72, 136, 32)):
            backward = Vector(regression.rotate((0, 0, -1), angles))
            for slope in (0, .4, 1.5, 4.0):
                local_view = Vector((-slope, .2, -1))
                view = Vector(regression.rotate(local_view / length(local_view), angles))
                for orthographic in (False, True):
                    for physical_distance in (0, .01, .5, 6, 50):
                        eye_distance = physical_distance if orthographic else physical_distance * dot(backward, view)
                        values = {"backward": backward, "i_view": view, "orthographic": float(orthographic), "i_eyeDepthDistance": eye_distance}
                        values["alignment"] = evaluate(alignment, values)
                        actual = evaluate(distance, values)
                        self.assertAlmostEqual(actual, physical_distance, places=10)
                        density = Vector((.9, .3, .35))
                        alpha = evaluate(opacity, {"_DepthFogDensity": density, "depthFogDistance": actual})
                        for channel, sigma in zip(alpha, density):
                            self.assertAlmostEqual(channel, 1 - math.exp(-sigma * physical_distance), places=11)
                    values["i_eyeDepthDistance"] = -1
                    self.assertEqual(evaluate(distance, values), 0)

    def test_surface_water_length_uses_both_selected_view_endpoints(self):
        source = regression.source(EMISSION)
        expression = next(value for value in re.findall(r"\bdepthFogDistance\s*=\s*([^;]+);", source) if "length(" in value)
        for angles in ((0, 0, 0), (32, 67, -19), (-55, -25, 77)):
            delta = Vector(regression.rotate((3, -4, 0), angles))
            for origin in (Vector((0, 0, 0)), Vector((80000, -2000, -40000))):
                values = {"surfacePosVS": origin, "scenePosVS": origin + delta, "sceneZ": 10, "i_pixelZ": 2}
                self.assertAlmostEqual(evaluate(expression, values), 5, places=9)
                # MSAA/foreground fallback must not turn a lateral UV offset into water thickness.
                for scene_z in (1, 2):
                    values["sceneZ"] = scene_z
                    self.assertEqual(evaluate(expression, values), 0)

    def test_depth_order_screen_bounds_and_foreground_fallback(self):
        helpers, emission = regression.source(regression.HELPERS), regression.source(EMISSION)
        for name, pairs in (("CrestIsDepthBehind", ((.2, .8), (.8, .2), (.4, .4))),
                            ("CrestHasSceneDepth", ((0, 0), (.5, 0), (1, 0)))):
            function = regression.body(helpers, name)
            branches = re.search(r"#if UNITY_REVERSED_Z(.*?)#else(.*?)#endif", function, re.S)
            self.assertIsNotNone(branches)
            for reverse in (False, True):
                expression = re.search(r"return\s+([^;]+);", branches[1 if reverse else 2])[1]
                for raw, surface in pairs:
                    actual = evaluate(expression, {"i_rawDepth": raw, "i_rawSurfaceDepth": surface})
                    expected = ((raw < surface) if reverse else (raw > surface)) if name == "CrestIsDepthBehind" else (raw != (0 if reverse else 1))
                    self.assertEqual(actual, expected)
        uv_expression = assignment(emission, "uvDepthRefract")
        half_texel = Vector((.5 / 960, .5 / 540))
        for uv in (Vector((.001, .999)), Vector((.5, .5)), Vector((.999, .001))):
            for offset in (Vector((-2, 3)), Vector((3, -2)), Vector((0, 0)), Vector((.01, -.02))):
                selected = evaluate(uv_expression, {"i_uvDepth": uv, "refractOffset": offset, "halfTexel": half_texel})
                for value, margin in zip(selected, half_texel):
                    self.assertGreaterEqual(value, margin)
                    self.assertLessEqual(value, 1 - margin)
        self.assertRegex(emission, r"uvBackgroundRefract = uvBackground;\s+uvDepthRefract = i_uvDepth;\s+rawDepth = i_rawDepth;")
        self.assertIn("CrestReconstructViewPosition(uvDepthRefract, reconstructionDepth)", emission)
        self.assertEqual(emission.count("CREST_MULTISAMPLE_SCENE_DEPTH(uvDepthRefract, rawDepth)"), 2)

    def test_float32_shallow_water_lengths_survive_large_world_translation(self):
        helper = regression.body(regression.source(regression.HELPERS), "CrestReconstructViewPosition")
        expression = re.search(r"return\s+([^;]+);", helper)[1]
        thickness = next(value for value in re.findall(r"\bdepthFogDistance\s*=\s*([^;]+);", regression.source(EMISSION)) if "length(" in value)

        def f32(value): return struct.unpack("f", struct.pack("f", value))[0]

        def compute_world(uv, depth, inverse):
            clip = (f32(f32(uv[0] * 2) - 1), f32(1 - f32(uv[1] * 2)), f32(depth), 1)
            homogeneous = [f32(sum(f32(f32(a) * b) for a, b in zip(row, clip))) for row in inverse]
            return Vector(f32(value / homogeneous[3]) for value in homogeneous[:3])

        witnessed_world_precision_loss = False
        for orthographic in (False, True):
            near, far = .2, 100
            a = -1 / (far - near) if orthographic else -near / (far - near)
            b = 1 - a * near if orthographic else near * far / (far - near)
            projection_inverse = ((1, 0, 0, 0), (0, 1, 0, 0), (0, 0, -1 / a, b / a), (0, 0, 0, 1)) if orthographic else (
                (1, 0, 0, 0), (0, 1, 0, 0), (0, 0, 0, -1), (0, 0, 1 / b, -a / b))
            for origin in (0, 80000, 1000000, 10000000):
                # Same local geometry, only the camera/world translation changes.
                inverse_view_projection = projection_inverse[:2] + (tuple(f32(x + origin * w) for x, w in zip(projection_inverse[2], projection_inverse[3])),) + projection_inverse[3:]
                for surface_depth in (1.5, 3, 8):
                    for expected in (.002, .01, .1):
                        points = []
                        for depth in (surface_depth, surface_depth + expected):
                            raw = f32(a * depth + b if orthographic else a + b / depth)
                            points.append(evaluate(expression, {"i_positionNDC": Vector((.5, .5)), "i_rawDepth": raw,
                                                                "UNITY_MATRIX_I_P": projection_inverse, "UNITY_MATRIX_I_VP": inverse_view_projection},
                                                   {"ComputeWorldSpacePosition": compute_world}))
                        result = evaluate(thickness, {"surfacePosVS": points[0], "scenePosVS": points[1], "sceneZ": surface_depth + expected, "i_pixelZ": surface_depth})
                        self.assertAlmostEqual(result, expected, delta=.00001, msg=(orthographic, origin, surface_depth, expected))
                        # Demonstrate the failure this test prevents using actual IEEE float32,
                        # not just a high-precision camera round trip.
                        old_world_length = abs(f32(origin + points[1][2]) - f32(origin + points[0][2]))
                        witnessed_world_precision_loss |= abs(old_world_length - expected) > .001
        self.assertTrue(witnessed_world_precision_loss)

    def test_sky_reconstruction_and_zero_density_stay_finite(self):
        emission = regression.source(EMISSION)
        sky_depth = assignment(emission, "reconstructionDepth")
        ray_scale = assignment(emission, "rayLengthPerEyeDepth")
        fog = next(value for value in re.findall(r"\bdepthFogDistance\s*=\s*([^;]+);", emission) if "CrestFiniteCameraFarDepth" in value)
        opacity = next(value for value in re.findall(r"\balpha\s*=\s*([^;]+);", emission) if "exp(" in value)
        far_helper = regression.body(regression.source(regression.HELPERS), "CrestFiniteCameraFarDepth")
        far_expression = re.search(r"return\s+([^;]+);", far_helper)[1]
        for raw in (0, 1):
            self.assertEqual(evaluate(sky_depth, {"hasSceneDepth": False, "rawDepth": raw}), .5)
        for far in (1000, 1e12, math.inf):
            finite_far = evaluate(far_expression, {"_ProjectionParams": Vector((1, .2, far, 0))})
            self.assertTrue(math.isfinite(finite_far))
            for ortho in (False, True):
                values = {"scenePosVS": Vector((4, 2, -.4)), "orthographic": float(ortho), "i_pixelZ": 2}
                values["rayLengthPerEyeDepth"] = evaluate(ray_scale, values)
                distance = evaluate(fog, values, {"CrestFiniteCameraFarDepth": lambda: finite_far})
                self.assertTrue(math.isfinite(distance))
                alpha = evaluate(opacity, {"_DepthFogDensity": Vector((0, .3, 0)), "depthFogDistance": distance})
                self.assertEqual(alpha[0], 0)
                self.assertEqual(alpha[2], 0)
                self.assertTrue(all(math.isfinite(channel) and 0 <= channel <= 1 for channel in alpha))
        fullscreen = regression.source(UNDERWATER + "UnderwaterEffect.hlsl")
        self.assertIn("CrestHasSceneDepth(rawDepth) ? rawDepth : 0.5", fullscreen)
        self.assertIn("float fogDistance = min(sceneZ, CrestFiniteCameraFarDepth());", fullscreen)

    def test_sky_view_direction_never_subtracts_large_world_positions(self):
        helper = regression.body(regression.source(regression.HELPERS), "CrestViewDirectionFromViewPosition")
        view_expression = assignment(helper, "viewVS")
        return_expression = re.search(r"return\s+([^;]+);", helper)[1]
        self.assertNotIn("_WorldSpaceCameraPos", helper)
        for angles in ((0, 0, 0), (25, 17, -64), (-41, 173, 39)):
            right, up, backward = (Vector(regression.rotate(axis, angles)) for axis in ((1, 0, 0), (0, 1, 0), (0, 0, 1)))
            for origin in (0, 80000, 10000000):
                inverse_view = tuple((right[k], up[k], backward[k], origin) for k in range(3)) + ((0, 0, 0, 1),)
                for orthographic in (False, True):
                    for ray_point in (Vector((0, 0, -.2)), Vector((.2, .1, -.2))):
                        values = {"orthographic": float(orthographic), "i_positionVS": ray_point, "UNITY_MATRIX_I_V": inverse_view}
                        values["viewVS"] = evaluate(view_expression, values)
                        actual = evaluate(return_expression, values)
                        local = Vector((0, 0, 1)) if orthographic else -ray_point / length(ray_point)
                        expected = right * local[0] + up * local[1] + backward * local[2]
                        for a, b in zip(actual, expected):
                            self.assertAlmostEqual(a, b, places=12)
                        self.assertAlmostEqual(length(actual), 1, places=12)
                        self.assertTrue(all(math.isfinite(value) for value in actual))
        for path in (UNDERWATER + "UnderwaterEffect.hlsl", UNDERWATER + "UnderwaterCurtain.shader"):
            self.assertIn("CrestViewDirectionFromViewPosition(", regression.source(path))

    def test_all_underwater_paths_share_ray_distance_and_resolved_depth(self):
        for path in (regression.OCEAN, UNDERWATER + "UnderwaterEffect.hlsl", UNDERWATER + "UnderwaterEffectIncludes.hlsl", UNDERWATER + "UnderwaterCurtain.shader"):
            self.assertIn("CrestWaterRayDistance(", regression.source(path), path)
            self.assertTrue("GetWorldSpaceNormalizeViewDir(" in regression.source(path) or "CrestViewDirectionFromViewPosition(" in regression.source(path), path)
        shared = regression.source(UNDERWATER + "UnderwaterEffectShared.hlsl")
        for sampling in ("CREST_MULTILOAD_DEPTH", "CREST_MULTILOAD_SCENE_DEPTH"):
            self.assertRegex(shared, r"rawDepth = " + sampling + r"\([^;]+\);\s+sceneZ = CrestLinearEyeDepth\(rawDepth\);")
        self.assertIn("hasCaustics = hasCaustics && CrestHasSceneDepth(rawDepth);", shared)

    def test_caustics_follow_snell_and_fade_in_deep_or_unlit_water(self):
        caustics = regression.body(regression.source(EMISSION), "ApplyCaustics")
        names = ("sunlight", "submerged", "refractiveRatio", "lightInWaterXZ", "transmittedSinSquared", "lightInWaterY", "lightPath", "lightProjection", "causticsStrength")
        equations = {name: assignment(caustics, name) for name in names}
        self.assertNotRegex(caustics, r"\.Sample\(", "Early receiver rejection requires explicit texture LODs")
        self.assertIn("if (submerged <= 0.0)", caustics)
        self.assertIn("if (transmittedSinSquared >= 1.0)", caustics)
        self.assertIn("sunlight <= 0.0", caustics)
        eta = 1 / 1.333
        maximum_slope = eta / math.sqrt(1 - eta * eta)
        for elevation in (-90, -.001, 0, .000001, .01, .1, 1, 10, 45, 89.99, 90):
            radians = math.radians(elevation)
            for intensity in (0, .01, 1, 8):
                previous = None
                for depth in (.1, .5, 2, 20, 200):
                    values = {"i_lightDir": Vector((math.cos(radians), math.sin(radians), 0)),
                              "_LightColor0": Vector((intensity,) * 3), "sceneDepth": depth,
                              "_RefractiveIndexOfAir": 1, "_RefractiveIndexOfWater": 1.333,
                              "_CausticsStrength": 3.2, "_DepthFogDensity": Vector((.9, .3, .35))}
                    for name in names:
                        values[name] = evaluate(equations[name], values)
                    self.assertAlmostEqual(length(values["lightInWaterXZ"]), eta * abs(math.cos(radians)), places=12)
                    self.assertLessEqual(length(values["lightProjection"]) / depth, maximum_slope + 1e-12)
                    self.assertTrue(all(math.isfinite(value) and value >= 0 for value in values["causticsStrength"]))
                    if intensity == 0 or elevation <= 0:
                        self.assertEqual(values["sunlight"], 0)
                        self.assertEqual(values["causticsStrength"], Vector((0, 0, 0)))
                    if previous is not None:
                        self.assertTrue(all(current <= old for current, old in zip(values["causticsStrength"], previous)))
                    previous = values["causticsStrength"]
        for depth in (-100, -.01, 0):
            self.assertEqual(evaluate(equations["submerged"], {"sceneDepth": depth}), 0)

    def test_caustic_modulation_cannot_produce_negative_radiance(self):
        caustics = regression.body(regression.source(EMISSION), "ApplyCaustics")
        expression = re.search(r"io_sceneColour.xyz\s*\*=\s*([^;]+);", caustics)[1]
        expression = re.sub(r"i_causticsTexture.SampleLevel\(cuv1.xy, cuv1.z\).xyz", "sampleOne", expression)
        expression = re.sub(r"i_causticsTexture.SampleLevel\(cuv2.xy, cuv2.z\).xyz", "sampleTwo", expression)
        for strength in (0, 3.2, 10):
            for average in (0, .07, 1):
                for sample_one in (0, .5, 1):
                    for sample_two in (0, .5, 1):
                        actual = evaluate(expression, {"causticsStrength": Vector((strength,) * 3), "_CausticsTextureAverage": average,
                                                       "sampleOne": Vector((sample_one,) * 3), "sampleTwo": Vector((sample_two,) * 3)})
                        self.assertTrue(all(value >= 0 and math.isfinite(value) for value in actual))
                        if strength == 0: self.assertEqual(actual, Vector((1, 1, 1)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read shader equations from this Git revision")
    args, remaining = parser.parse_known_args()
    regression.SOURCE_REF = args.ref
    print("Optical quality: source-driven numerical invariants; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
