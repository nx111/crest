# URP migration static check results

Date: 2026-10-10. Base: `db0658ff0b2e93e4a9e28cc2867509658b0ecc00`.

| Check | Result |
| --- | --- |
| C# 9 syntax / conditional directives | 145 C# files × 4 configurations (Unity 6.0/6.6, Editor/Player); **0 errors** |
| Shader source structure | 99 shader/HLSL/compute files; balanced preprocessor and shader-program blocks; **0 errors** |
| Local shader includes | 184 references resolve; **0 missing files** |
| Unsupported shader constructs | No active `GrabPass` or Surface Shader pragmas |
| Assembly and package JSON | 6 assembly definitions plus manifest parse successfully; no duplicate assembly references |
| New Unity assets | 12 new asset/folder entries have valid distinct stable metadata GUIDs |
| Whitespace | `git diff --check` / staged equivalent: passed |
| Unity headless import, pipeline creation and validation | **Passed locally with Unity 6000.6.2f1 / URP 17.6.0** |
| Unity assembly/API binding | **Passed locally with Unity 6000.6.2f1** |
| Example asset conversion and pipeline validation | **Passed: 8 materials, 3 prefab assets; a repeated conversion made no further changes** |
| Unity shader compiler | **Passed for exercised D3D11 variants and passes on 51 visible renderer materials** |
| Editor GPU rendering | **Passed: 7 example scenes and 2 additional underwater frames** |
| Full variant coverage / player builds / Android device | **Not run** |

During handoff, the C# checker was compiled directly with the installed SDK's `csc.dll`
and run against the source tree. The `dotnet run` restore bootstrap encountered a
NuGet environment-path error, so the check used local reference assemblies without
package restoration. Two framework-unification warnings occurred while building
the checker against .NET 10 with the SDK's .NET 9 Roslyn binaries; the checker
itself ran successfully. These were not Unity project compilation results.

During local integration, `python tools/check_urp.py`, the documented `dotnet run`
command and `git diff --check db0658ff HEAD` passed again. The bundle SHA-256 and
imported Git tree matched `commit-verification.json`.

A temporary project copy was imported with Unity 6000.6.2f1 in batch mode with
`-nographics`. Project C# compilation, `Crest.CrestURPSetup.CreatePipeline` and
`Crest.CrestURPSetup.ValidatePipeline` completed successfully; both editor runs
exited with code 0. URP/Core remained at 17.6.0,
while Unity automatically upgraded older dependencies, including Burst to 2.0.0,
Mathematics to 1.4.0, Input System to 1.20.0 and Splines to 2.9.0. The committed
example project now records these resolved package versions and targets 6000.6.2f1.
The handoff's exact 6000.6.0f1 editor target, player builds and device rendering remain untested.

Manual source review covered the new URP API calls against Unity's official URP
17.6 source, render pass ordering, camera filters, color-copy lifetime, texture
imports, MSAA descriptor handling, reflection state restoration, shader lighting
and depth interfaces, and preservation of the existing simulation/query code.

The example update was additionally checked in a temporary project copy with a
Play Mode rendering test on D3D11, outside batch mode so Crest creates its ocean
tiles. The test loaded `boat`, `BoatWakes`, `threeboats`, `Examples`, `LakesAndRivers`,
`main` and `whirlpool`; compiled all passes on 51 visible renderer materials;
and rendered 960 x 540 images using `UniversalRenderPipeline.SingleCameraRequest`
after simulation updates. The main scene was also rendered underwater with
`EnableShaderAPI` off and on. Assertions checked active ocean tiles, URP camera
data, material shaders, image variation and error-shader pixels. The test passed
with editor exit code 0; the final log contained no shader errors, runtime
exceptions or shadow render-data order warnings. Nine captures were inspected.

These checks exposed and verified fixes for mismatched shadow helper signatures
on D3D11 and camera state changed by explicit simulation draws, which had
corrupted the main scene and underwater mask. The renderer now restores camera
properties, URP view/projection matrices and the camera viewport around those draws.

The smoke checks cover basic rendering and exercised variants. Complete the
[local validation checklist](URP-MIGRATION.md) for full visual and device regression.
The generated pipeline and global settings
assets are included in the example project and assigned in Graphics and all quality
levels. Converted example materials retain their asset GUIDs. Built-in default
material references in boat prefabs use URP materials; the shared base camera includes
URP camera data with MSAA disabled.

## Targeted shading regressions

Base for these fixes: `175fe207d111f8a4a91386b2aa0a1e4e8987f8a3` on `urp`.
The earlier Unity/D3D11 results above predate these changes.

| Check | Result |
| --- | --- |
| `python tools/check_urp_regressions.py -v` | **9 tests passed**: reconstruction, per-camera MSAA, shadow variants/coordinates and probe-blending data flow |
| Numerical reconstruction | **243 points passed** across the ocean, underwater effect and Curtain expressions; identity/rotated cameras, off-axis rays and large world offsets |
| Numerical atlas coordinates | Single shadow, 1/2/3/4-cascade screen-shadow queries, cascade keywords before Crest globals initialize, and out-of-cascade sentinel checked |
| `python tools/check_urp.py` | **0 errors**; 99 shader/HLSL/compute files, 184 local includes, 6 assembly definitions and 12 new asset metadata entries |
| Existing C# 9 syntax checker | **0 errors**; 145 files × 4 Unity Editor/Player define configurations |
| Whitespace | `git diff --check`: passed |
| Unity 6 import/API binding, HLSL compilation, rendering and player variants for these fixes | **Not run**; complete the focused [local review items](URP-MIGRATION.md#本地复核清单) |

The new numerical regression fails against the base source: the central view ray
at eye depth `0.4` reconstructs to `-0.4` behind the camera. The fixed expression
reconstructs to `+0.4` in front of it. MSAA, shadow-variant and probe-blending source
checks also reject the base revision. This is regression evidence, not a GPU result.

The current cloud environment has Unity 2022.3's standalone .NET 6/Roslyn tools,
but no .NET 10 SDK or the target Unity 6 editor. The existing C# checker was built
and run with those standalone tools without starting Unity or waiting for Hub login.
Syntax parsing does not bind Unity assemblies.

The shadow sampling signature, screen/transparent coordinate routing, probe
reflection overload and `UniversalShadowData.mainLightShadowCascadesCount` were
reviewed against official URP 17.6 source at
[`a7e4c051`](https://github.com/Unity-Technologies/Graphics/tree/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.universal).
World-space caustics and shadow accumulation read the already declared main-light
atlas directly; they do not depend on the later screen-space shadow texture.

## Rendering quality follow-up — 2026-10-11

Starting revision: `3dd1c82b` on `urp`. The historical “Not run” entries above
describe earlier checks; the current code has now been compiled and rendered in
a disposable project copy using Unity **6000.6.2f1 / URP 17.6.0**, **GTX 1650 / D3D11**.
The source project was not given a Library or changed by the test editor.
The full audit is in [URP-QUALITY-AUDIT.md](URP-QUALITY-AUDIT.md).

| Check | Result |
| --- | --- |
| Current C# API binding / Play Mode | **Passed**; renderer feature and quality runner compile in the target editor |
| `python tools/check_urp.py` | **0 errors**; 100 shader/HLSL/compute files, 185 local includes, 6 asmdefs, 14 new Unity resource metadata entries |
| `python tools/check_urp_regressions.py -v` | **9 tests passed**, including the existing 243-point reconstruction regression |
| `dotnet run --project tools/StaticChecks -- crest/Assets` | **0 syntax errors**; 146 files × 4 Unity Editor/Player configurations |
| Whitespace | `git diff --check`: **passed** |
| GPU local lights | **Passed**: point/spot illumination, zero intensity, range/cone rejection, independent local highlights, additional spot shadows, per-vertex asset mode, basic Forward+ cluster loop |
| GPU reflection/refraction | **Passed**: local probe box projection, ordinary queue-3000 URP transparent capture, wave-normal distortion, per-camera opaque fallback |
| GPU underwater/cameras | **Passed**: both fullscreen composition paths; MSAA 1/2/4/8; render scale 0.75; orthographic rendering smoke check |
| Current examples | **All 7 scenes passed** exercised material-pass compilation, ocean existence and error-shader checks |
| Original Built-in surface comparison | **Mean per-pixel RGB absolute-difference sum: 0** at 960 × 540, using the baseline shader/includes and controlled single-sun/fixed-cubemap settings |
| Editor result | **`CREST_QUALITY_RESULT PASS`**, 37 captures, exit code 0; no runtime/shader/Render Graph errors |
| Full variants / player builds / device / performance | **Not run** |

The original shader comparison changes only the reference shader name, switches
the actual pipeline, and retains identical simulation geometry, time, camera,
material properties and active keywords. Foam, scene refraction, shadows,
caustics, albedo and planar reflection are disabled in that test. This establishes
agreement for the isolated surface case, not complete original-project equivalence.

The final run reports one unrelated Unity Search indexing exception from
`UnityEditor.Search.SearchDatabase` as an `EDITOR` note. Rendering errors are fatal
and none were recorded. The raw assertion record and 13 selected previews are
preserved under [_media/urp-quality/2026-10-11](_media/urp-quality/2026-10-11/results.txt).
See [UnityQualityTests](../tools/UnityQualityTests/README.md) to reproduce the run.
