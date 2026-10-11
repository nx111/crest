#!/usr/bin/env python3
"""Depth-cache geometry and precision regressions from actual source expressions.

Uses independent orthographic projection matrices and IEEE binary16/binary32
round trips. No Unity API binding, rendering, or texture import is executed.
--ref can demonstrate the failures on an earlier source revision.
"""

import argparse
import math
import re
import struct
import sys
import unittest

import check_foam_quality as shader

ROOT = "crest/Assets/Crest/Crest/"
CACHE = ROOT + "Scripts/LodData/OceanDepthCache.cs"
COPY = ROOT + "Shaders/OceanInputs/Resources/CopyDepthIntoCache.shader"
HEIGHT_USERS = (
    ROOT + "Shaders/OceanHelpersNew.hlsl",
    ROOT + "Shaders/OceanInputs/AnimWavesSpectrum.shader",
    ROOT + "Shaders/OceanInputs/GerstnerShared.hlsl",
    ROOT + "Shaders/OceanInputs/Resources/AnimWavesGerstner.shader",
    ROOT + "Shaders/OceanInputs/Resources/AnimWavesGerstnerGeometry.shader",
    ROOT + "Shaders/Resources/UpdateFoam.compute",
)
SURFACE_USERS = (
    ROOT + "Shaders/Ocean.shader",
    ROOT + "Shaders/Underwater/UnderwaterMaskShared.hlsl",
    ROOT + "Shaders/OceanSurfaceAlpha.shader",
)
GEOMETRY_USERS = (
    ROOT + "Shaders/OceanInputs/AnimWavesRemoveGeometry.shader",
    ROOT + "Shaders/OceanInputs/AnimWavesSetHeightToGeometry.shader",
    ROOT + "Shaders/OceanInputs/Resources/AnimWavesSetBaseWaterHeightUsingGeometry.shader",
    ROOT + "Shaders/OceanInputs/Resources/ClipSurfaceConvexHull.shader",
    ROOT + "Shaders/OceanInputs/Resources/ClipSurfaceSignedDistance.shader",
)


def floating(value, precision):
    code = "e" if precision.startswith("half") else "f"
    return struct.unpack("<" + code, struct.pack("<" + code, value))[0]


def offset_helper(text, name):
    """Read the actual absolute offset parameter, sample and accumulation types."""
    signatures = re.finditer(r"void " + name + r"\(([^{}]+)\)\s*\{", text)
    signature = next(item for item in signatures if "io_seaLevelOffset" in item[1])
    precision = re.search(r"inout (half|float) io_seaLevelOffset", signature[1])[1]
    block = shader.braced(text, signature.end() - 1)[0]
    sample = re.search(r"\b(half2|float2) terrainHeight_seaLevelOffset\s*=", block)
    assignment = re.search(r"io_seaLevelOffset\s*(\+=|=)\s*([^;]+);", block)
    expression = re.sub(r"i_oceanDepthSampler.SampleLevel\([^;]+?\)\.y", "depthSample.y", assignment[2])

    def accumulate(previous, weight, height):
        height = floating(height, "float")
        values = {"i_wt": weight, "depthSample": shader.Vec((0.0, height)),
                  "terrainHeight_seaLevelOffset": shader.Vec((0.0, floating(height, sample[1] if sample else "float")))}
        value = shader.evaluate(expression, values)
        if assignment[1] == "+=":
            value += floating(previous, precision)
        return floating(value, precision)

    return accumulate


def reversed_branch(text, reversed_z):
    lines, active = [], True
    for line in text.splitlines():
        if line.strip() == "#if UNITY_REVERSED_Z":
            active = reversed_z
        elif line.strip() == "#else":
            active = not active
        elif line.strip() == "#endif":
            active = True
        elif active:
            lines.append(line)
    return "\n".join(lines)


def project_depth(eye_distance, near, far, gl_clip, reversed_z):
    """Apply the Z row of an orthographic matrix, then the depth viewport map.

    Eye-space forward is -Z. GL clip Z spans [-1, 1]; the other convention
    spans [0, 1]. Reversing the matrix swaps the near/far endpoints.
    """
    span = far - near
    row = (0.0, 0.0, -2.0 / span, -(far + near) / span) if gl_clip else (0.0, 0.0, -1.0 / span, -near / span)
    clip = sum(a * b for a, b in zip(row, (0.0, 0.0, -eye_distance, 1.0)))
    if reversed_z:
        clip = -clip if gl_clip else 1.0 - clip
    return 0.5 * clip + 0.5 if gl_clip else clip


class DepthCacheQualityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cache = shader.source(CACHE)
        cls.copy = shader.source(COPY)

    def capture_height(self, camera_y, near, far, device_depth, reversed_z, offset):
        # Read the actual camera-to-height binding expressions in C#.
        values = {"cameraY": camera_y, "near": near, "far": far}
        for name in ("ymax", "ymin"):
            expression = re.search(r"float " + name + r"\s*=\s*([^;]+);", self.cache)[1]
            values[name] = shader.evaluate(expression.replace("_camDepthCache.transform.position.y", "cameraY"), values)
        values = {
            "_HeightNearHeightFar": shader.Vec((values["ymax"], values["ymin"])),
            "_HeightOffset": offset, "deviceDepth": device_depth,
            "lerp": lambda a, b, weight: a + (b - a) * weight,
        }
        if re.search(r"\bfloat CrestCacheDepthToHeight\(", self.copy):
            return shader.execute(reversed_branch(shader.body(self.copy, "CrestCacheDepthToHeight"), reversed_z), values)

        # Evaluate the original implementation as well, so --ref tests numerical
        # behavior instead of only failing because the helper was renamed.
        old = dict(values, z=device_depth, _CustomZBufferParams=shader.Vec((1 - far / near, far / near)))
        values["linear01Z"] = shader.execute(shader.body(self.copy, "CustomLinear01Depth"), old)
        signature = re.search(r"\bfrag\s*\([^{}]*\)\s*:\s*SV_Target\s*\{", self.copy)
        fragment = reversed_branch(shader.braced(self.copy, signature.end() - 1)[0], reversed_z)
        expression = re.search(r"altitude\s*=\s*([^;]+);", fragment)[1]
        return shader.evaluate(expression, values) - offset

    def test_orthographic_heights_match_geometry_on_four_depth_conventions(self):
        for gl_clip in (False, True):
            for reversed_z in (False, True):
                for near, far in ((0.01, 10.0), (0.3, 10000.0), (7.0, 120.0)):
                    for camera_y, relative_offset in ((100.0, 0.0), (1234.0, 1200.0), (-120.0, -200.0)):
                        for fraction in (0.0, 0.01, 0.2, 0.5, 0.999, 1.0):
                            distance = near + (far - near) * fraction
                            expected_height = camera_y - distance
                            depth = project_depth(distance, near, far, gl_clip, reversed_z)
                            cached = self.capture_height(camera_y, near, far, depth, reversed_z, relative_offset)
                            self.assertAlmostEqual(cached + relative_offset, expected_height, delta=1e-8,
                                                   msg=(gl_clip, reversed_z, near, far, fraction))

    def test_sloping_shoreline_has_no_near_plane_shift(self):
        # A 1:20 beach crosses sea level at x=0. Near-plane bias in height moves
        # the inferred shoreline by 20 times that bias (6m with a 0.3m near plane).
        near, far, camera_y = 0.3, 10000.0, 100.0
        for gl_clip in (False, True):
            for reversed_z in (False, True):
                for x in (-10.0, -2.0, 0.0, 2.0, 10.0):
                    terrain_y = x / 20.0
                    raw = project_depth(camera_y - terrain_y, near, far, gl_clip, reversed_z)
                    decoded = self.capture_height(camera_y, near, far, raw, reversed_z, 0.0)
                    self.assertAlmostEqual(decoded * 20.0, x, delta=1e-8)

    def test_absolute_heights_are_subtracted_before_half_precision(self):
        count = 0
        for path in HEIGHT_USERS:
            text = shader.source(path)
            samples = list(re.finditer(r"\b(half2|float2)\s+terrainHeight_seaLevelOffset\s*=", text))
            self.assertTrue(samples, path)
            for index, sample in enumerate(samples):
                section = text[sample.end():samples[index + 1].start() if index + 1 < len(samples) else len(text)]
                assignment = re.search(r"(?:const\s+)?(?:(half|float)\s+)?(?:depth|waterDepth|signedOceanDepth|io_oceanDepth)\s*(?:\+=|=)\s*([^;]+);", section)
                self.assertIsNotNone(assignment, path)
                for ocean_y, local_level, depth in ((0.0, 1024.0, 0.25), (8192.0, 0.0, 0.125),
                                                    (0.0, -1024.0, 0.03125), (0.0, 8192.0, 0.0625)):
                    # These exact binary fractions are representable in the R32
                    # source and as the final half depth, but not as half heights.
                    bottom = floating(ocean_y + local_level - depth, "float")
                    level = floating(local_level, "float")
                    height_sample = shader.Vec((floating(bottom, sample[1]), floating(level, sample[1])))
                    values = {"_OceanCenterPosWorld": shader.Vec((0.0, ocean_y, 0.0)),
                              "terrainHeight_seaLevelOffset": height_sample, "i_wt": 1.0,
                              "disp": shader.Vec((0.0, 0.0, 0.0))}
                    actual = shader.evaluate(assignment[2], values)
                    # Retaining a half output is fine after the height subtraction.
                    actual = floating(actual, assignment[1] or "half")
                    self.assertEqual(actual, depth, (path, index, ocean_y, local_level))
                count += 1
        self.assertEqual(count, 8)

    def test_flat_elevated_water_has_no_artificial_base_slope(self):
        text = shader.source(HEIGHT_USERS[0])
        signature = re.search(r"void SampleSeaDepth\([^{}]*io_seaLevelDerivs[^{}]*\)\s*\{", text)
        section = shader.braced(text, signature.end() - 1)[0]
        precision = re.search(r"\b(half2|float2)\s+terrainHeight_seaLevelOffset\s*=", section)[1]
        expression = re.search(r"io_seaLevelDerivs.x\s*\+=\s*([^;]+);", section)[1]
        expression = expression.replace("i_cascadeParams._texelWidth", "texelWidth")
        for level in (1024.25, 8192.5, -1024.125):
            for texel in (0.25, 1.0, 4.0):
                value = shader.evaluate(expression, {"seaLevelOffset_x": floating(level, "float"),
                                                     "terrainHeight_seaLevelOffset": shader.Vec((0.0, floating(level, precision))),
                                                     "texelWidth": texel, "i_wt": 1.0})
                self.assertEqual(value, 0.0)

    def test_surface_mask_and_alpha_share_precise_elevated_geometry(self):
        helpers = shader.source(HEIGHT_USERS[0])
        for path in SURFACE_USERS:
            text = shader.source(path)
            local = re.search(r"\b(half|float) seaLevelOffset\s*=", text)[1]
            function = re.search(r"\b(SampleSeaDepth|SampleSeaLevelOffset)\([^;]+, seaLevelOffset\b[^;]*\);", text)[1]
            accumulate = offset_helper(helpers, function)
            height_expression = re.search(r"\b(?:o\.)?worldPos.y\s*\+=\s*([^;]*seaLevelOffset[^;]*);", text)[1]
            for ocean_y in (0.0, 8192.0, -1024.0):
                for levels in ((1024.25, 1024.5), (8192.5, 8193.0), (-1024.125, -1024.25)):
                    for weights in ((1.0, 0.0), (0.75, 0.25), (0.5, 0.5), (0.0, 1.0), (0.5, 0.25)):
                        offset = 0.0
                        for weight, level in zip(weights, levels):
                            offset = floating(accumulate(offset, weight, level), local)
                        value = shader.evaluate(height_expression, {"seaLevelOffset": offset,
                                               "_OceanCenterPosWorld": shader.Vec((0.0, ocean_y, 0.0))})
                        # The alpha mesh starts at zero height; the surface/mask
                        # tile matrices already include the ocean's base height.
                        initial_height = 0.125 + (0.0 if "_OceanCenterPosWorld" in height_expression else ocean_y)
                        actual = floating(initial_height + value, "float")
                        expected = floating(ocean_y + 0.125 + sum(w * h for w, h in zip(weights, levels)), "float")
                        self.assertEqual(actual, expected, (path, ocean_y, levels, weights))

    def test_single_depth_sample_retains_absolute_offset(self):
        accumulate = offset_helper(shader.source(HEIGHT_USERS[0]), "SampleSingleSeaDepth")
        for level in (1024.25, 8192.5, -1024.125):
            self.assertEqual(accumulate(0.0, 1.0, level), floating(level, "float"))

    def test_height_and_clipping_inputs_use_the_same_elevated_plane(self):
        for path in GEOMETRY_USERS:
            text = shader.source(path)
            precision = re.search(r"\b(half|float) seaLevelOffset\s*=", text)[1]
            displacement = re.search(r"float (?:height|addHeight)\s*=\s*([^;]+);", text)
            clipping = re.search(r"(?:surfacePositionWS|positionWS).y\s*\+=\s*([^;]*seaLevelOffset[^;]*);", text)
            for ocean_y, level in ((0.0, 1024.25), (8192.0, 8192.5), (-1024.0, -1024.125)):
                world_y = ocean_y + level + 0.125
                values = {"seaLevelOffset": floating(level, precision), "_Weight": 1.0,
                          "worldPos": shader.Vec((0.0, world_y, 0.0)),
                          "_OceanCenterPosWorld": shader.Vec((0.0, ocean_y, 0.0))}
                if displacement:
                    actual = shader.evaluate(displacement[1].replace("input.worldPos", "worldPos"), values)
                    self.assertEqual(floating(actual, "half"), 0.125, path)
                elif clipping:
                    value = shader.evaluate(clipping[1], values)
                    initial_height = 0.125 + (0.0 if "_OceanCenterPosWorld" in clipping[1] else ocean_y)
                    self.assertEqual(floating(initial_height + value, "float"), world_y, path)
                else:
                    expression = re.search(r"return (half4\([^;]+\));", text)[1]
                    values["half4"] = lambda *items: shader.Vec(floating(item, "half") for item in items)
                    actual = shader.evaluate(expression.replace("input.worldPos", "worldPos"), values)
                    self.assertEqual(actual.y, 0.125, path)

    def test_shadow_bleeding_classification_preserves_terrain_height(self):
        helpers = shader.source(HEIGHT_USERS[0])
        text = shader.source(ROOT + "Shaders/ShaderLibrary/UpdateShadow.hlsl")
        sample_precision = re.search(r"SampleTerrainHeight\([^{}]*inout (half|float) io_terrainHeight", helpers)[1]
        locals_ = re.findall(r"\b(half|float) terrainHeight\s*=", text)
        self.assertEqual(len(locals_), 2)
        parameter = re.search(r"ComputeShadow\([^{}]*const (half|float) i_terrainHeight", text)[1]
        above_expression = re.search(r"if\s*\((i_positionWS.y\s*>[^)]+)\)", text)[1]
        below_expression = re.search(r"if\s*\((i_positionWS.y\s*<[^)]+)\)", text)[1]
        for water_y in (1024.0, 8192.0, -1024.0):
            # The original surface lies above the terrain, but the jittered
            # sample lands inside a 12.5cm higher bank. It must reject shadows.
            original_ground, jittered_ground = water_y - 0.0625, water_y + 0.0625
            original = floating(floating(floating(original_ground, sample_precision), locals_[1]), parameter)
            jittered = floating(floating(jittered_ground, sample_precision), locals_[0])
            values = {"i_positionWS": shader.Vec((0.0, water_y, 0.0)),
                      "i_terrainHeight": original, "terrainHeight": jittered}
            classified = shader.evaluate(above_expression, values) and shader.evaluate(below_expression, values)
            self.assertEqual(classified, original_ground < water_y < jittered_ground)

    def test_baked_readback_preserves_float_capture_values(self):
        texture = re.search(r"new Texture2D\(rt.width, rt.height, TextureFormat\.(\w+),[^;]+", self.cache)[1]
        precision = {"RGBAHalf": "half", "RGBAFloat": "float", "RFloat": "float"}[texture]
        for height in (0.125, -0.03125, 100.015625, -256.03125, 1024.25, -8192.5):
            source_value = floating(height, "float")
            # EXR OutputAsFloat widens the readback value; it cannot undo an
            # earlier conversion of the render target into a half CPU texture.
            round_trip = floating(floating(source_value, precision), "float")
            self.assertEqual(round_trip, source_value)

    def test_default_import_preserves_capture_resolution(self):
        assignment = re.search(r"ti.maxTextureSize\s*=\s*([^;]+);", self.cache)
        for original_limit in (2048, 8192):
            for width, height in ((512, 512), (3072, 3072), (4096, 4096), (8192, 8192)):
                limit = original_limit
                if assignment:
                    expression = assignment[1].replace("Mathf.Max", "max").replace("Mathf.NextPowerOfTwo", "nextPowerOfTwo")
                    expression = expression.replace("ti.maxTextureSize", "originalLimit").replace("rt.width", "width").replace("rt.height", "height")
                    limit = shader.evaluate(expression, {"originalLimit": original_limit, "width": width, "height": height,
                                                         "nextPowerOfTwo": lambda value: 1 << (value - 1).bit_length()})
                self.assertGreaterEqual(limit, max(width, height))
                self.assertGreaterEqual(limit, original_limit)

    def test_bilinear_cache_edges_do_not_wrap_unrelated_terrain(self):
        assignment = re.search(r"result.wrapMode\s*=\s*TextureWrapMode\.(\w+);", self.cache)
        if assignment is None:
            self.skipTest("Previous source uses Unity's native default; that default was not GPU-verified.")
        mode = assignment[1]
        # The leftmost texel is a 2m deep channel; the opposite side is dry land.
        # At the texture boundary, Repeat invents a +1m land barrier by blending
        # them. Clamp keeps the nearest captured -2m height.
        heights = (-2.0, -1.0, 1.0, 4.0)
        for uv in (0.0, 0.02, 0.98, 1.0):
            coordinate = uv * len(heights) - 0.5
            lower = math.floor(coordinate)
            blend = coordinate - lower
            def sample(index):
                index = min(max(index, 0), len(heights) - 1) if mode == "Clamp" else index % len(heights)
                return heights[index]
            actual = sample(lower) * (1 - blend) + sample(lower + 1) * blend
            self.assertEqual(actual, heights[0] if uv < 0.5 else heights[-1])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ref", help="Read source files from this Git revision")
    args, remaining = parser.parse_known_args()
    shader.SOURCE_REF = args.ref
    print("Depth-cache regressions: geometry/precision source evaluation only; no Unity/GPU execution.", flush=True)
    unittest.main(argv=[sys.argv[0], *remaining])
