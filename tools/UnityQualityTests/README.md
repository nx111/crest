# Unity rendering quality checks

`QualityRunner.cs` exercises the actual ocean shaders, Render Graph passes and
camera rendering on a GPU. It targets Unity **6000.6.2f1 / URP 17.6.0**, the pinned
versions of the example project. Earlier GPU results recorded on 2026-10-11 are
in [URP-QUALITY-AUDIT.md](../../docs/URP-QUALITY-AUDIT.md).
User-supplied tests of `90a93293` subsequently covered 322 Editor assertions and
Windows D3D11/D3D12 and Android Vulkan Players; see the
[runtime follow-up](../../docs/URP-RUNTIME-FOLLOWUP.md) for evidence and limits.
The supplied expanded Editor/Player test sources are not yet in this repository,
so this smaller runner does not reproduce all of those cases. The latest query
lifecycle and runner startup fixes still require a new Unity/device run.

The planar reflection, normal sampling, depth-cache and underwater Fresnel
changes also need the [additional manual visual/performance checks](../../docs/URP-REFLECTIONS-AND-SHALLOW-WATER.md#6-本地画面与性能验收).
This runner does not automate their mip/roughness sweeps, elevated shallow-water
scenes, EXR round trips or reflection-resource recovery. Offline compilation and
numerical results for that batch are recorded in the linked report.

The wave-normal, FFT/Gerstner and foam changes require the
[wave and foam manual checks](../../docs/URP-WAVES-AND-FOAM-STABILITY.md#6-本地画面与性能验收).
This runner does not automate long-running Gerstner precision, FFT loop/bake/CPU-query
comparisons, curved-flow transport, emitter-frequency sweeps, foam LOD/phase coverage
or temporal foam-highlight evaluation. Re-bake existing FFT collision assets before
reviewing them against the corrected waves.

The latest detail-normal reorientation, per-phase local foam lighting and baked
FFT query fixes require the [surface detail and query checks](../../docs/URP-SURFACE-DETAIL-AND-WAVE-QUERIES.md#6-本地画面与性能验收).
This runner does not automate steep-wave detail orientation, local-light/Flow
coverage sweeps, baked query coordinate/time boundaries or CPU query profiling.
The Built-in comparison below now isolates common lighting with flat shading
normals; it does not validate the new normal model's appearance.

Run it **only in a disposable project copy**. The runner changes scenes, camera
state, Graphics/Quality pipeline assignments, renderer settings and material
properties, then exits Unity. It is a rendering diagnostic, not an interactive
test for an open production project.

## Prepare and run

From the repository root in PowerShell, with the source Unity project closed:

```powershell
# Use a new path; never point this at the source project or an existing game.
$qualityProject = Join-Path $env:TEMP ('crest-quality-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $qualityProject | Out-Null
foreach ($directory in @('Assets', 'Packages', 'ProjectSettings')) {
    Copy-Item -LiteralPath (Join-Path 'crest' $directory) -Destination $qualityProject -Recurse
}
New-Item -ItemType Directory -Path (Join-Path $qualityProject 'Assets/Editor') -Force | Out-Null
Copy-Item -LiteralPath 'tools/UnityQualityTests/QualityRunner.cs' -Destination (Join-Path $qualityProject 'Assets/Editor/QualityRunner.cs')

# Optional original Built-in surface comparison; requires the baseline Git commit.
python tools/UnityQualityTests/prepare_builtin_reference.py $qualityProject

# Replace this editor path with your Unity 6000.6.2f1 installation.
$unityEditor = 'E:/Games/Unity/Editor/6000.6.2f1/Editor/Unity.exe'
$qualityLog = Join-Path $qualityProject 'quality-gpu.log'
$qualityProcess = Start-Process -FilePath $unityEditor -ArgumentList @(
    '-force-d3d11', '-projectPath', $qualityProject,
    '-crestSpecularAA', # Optional diagnostic; remove to skip its extra captures.
    '-executeMethod', 'QualityRunner.Start', '-logFile', $qualityLog
) -WindowStyle Normal -PassThru
$qualityProcess.Id
```

The initial import may take several minutes. Do **not** add `-batchmode`,
`-nographics` or `-quit`: the test enters Play Mode to create Crest's ocean tiles,
needs a graphics device, and exits itself after the captures. A successful run
prints `CREST_QUALITY_RESULT PASS`, writes `QualityCaptures/results.txt` and PNGs,
and exits with code 0. A failed assertion or rendering exception exits with code 1.
The runner installs its log listener before opening scenes, defers that initialization
until the editor UI is ready, and preserves startup failures across the Play Mode
domain reload. Inspect the complete process log after exit as well as the results
file: native shutdown leaks can occur after the managed test reports PASS.

For the native query and shutdown audit, copy `RuntimeQueryAudit.cs` outside Editor
and `AuditQueryCoordinates.compute` into `Assets/Resources` in the disposable copy.
Add `-crestQueryOnly -crestQuitAudit -crestLeakStacks` to run just these checks.
The shutdown audit deliberately leaves a live provider and an already-cleaned
provider with pending native requests, then verifies their resources in the quit
event. Full quality checks remain the default when `-crestQueryOnly` is absent.

The Player audit additionally uses `PlayerAudit.cs`, `PlayerRetest.cs` and
`PlayerGpuContracts.cs` outside Editor; install its builders in `Assets/Editor`.
Use `-crestQueryAudit -crestQuitAudit -crestFoamStorage -crestPerformanceAB` for
the extended diagnostics. Android `run_android_audit.py` accepts these via `--extra`,
checks the app's own logcat including native teardown, and reconnects a dropped
Wi-Fi ADB endpoint. `-crestBackgroundAudit` exercises Home/resume on that test app.
Run all Unity builds serially, especially on a mechanical disk.

Add `-crestScreenAudit` to save the actual game screen before and after the first
`SingleCameraRequest` capture. Inspect `screen-main-before-offscreen.png` and
`screen-main-after-offscreen.png` as well as the offscreen `scene-main.png`.
Finite offscreen pixels alone cannot detect a camera-state error affecting the
device's main framebuffer. An ADB screenshot also verifies the phone's displayed
image; the driver's first `device-screen.png` may still show the Unity splash.
On Android, the screen-capture API appends the app's persistent directory, so the
audit passes a relative filename after resolving the host's `/sdcard` alias.
It also checks that both screen files were saved.

For a reported brief Windows flash, also install `PlayerTemporalAudit.cs` and
add `-crestTemporalAudit`. It records every actual game frame through asynchronous
readback, adds 360 steady Main frames, and writes 320x180 JPEG previews to
`game-frames.zip` with camera, scene and test-phase metadata. Missing frames or
readback errors fail the recorder check. Review transitions as well as static
frames; passing pixel checks does not establish temporal stability. Recording
adds GPU/readback/encoding cost, so use a separate run for performance timing.
It replaces `-crestScreenAudit` when both are requested, avoiding conflicting
screen-capture operations in the same frame. `analyze_game_frames.py` ranks
same-camera, same-scene three-frame changes for visual review and preserves
camera cuts as metadata. JPEGs retain backend readback row order; orient review
figures appropriately. Image-difference scores alone do not certify stability.

The performance audit records requested, compatible and actual foam formats.
Its temporary settings survive ocean reconstruction and are restored afterward.
If the device selects a half-float fallback for an R32 request, the full-scene
R32 cost comparison is explicitly skipped. The separate small compute decay
test has different filtering/texture support requirements and does not establish
that the full ocean's R32 texture is supported.

`MinimalAndroidBuilder.cs`, `MinimalAndroidLifetime.cs` and `run_minimal_android.py`
provide a separate empty-project Android startup control, without Crest or URP.
The example project's `Development/Editor/AndroidAssetPackDependency.cs` works
around the confirmed Unity 6000.6.2f1 AssetPackManager startup error by adding the
official Play Asset Delivery 2.3.0 dependency if it is absent. It applies only to
that engine version; keep it in the project when building the Android example.

If the reference extractor was omitted, the run explicitly reports the Built-in
comparison as skipped. The extractor copies the original `Ocean.shader` and its
include files from `db0658ff0b2e93e4a9e28cc2867509658b0ecc00`, changing only the
shader name. It does not replace Crest runtime scripts or scene assets.

## Coverage

- Actual point/spot contributions on an isolated water surface; intensity zero,
  light range, spotlight cone, local highlights with sun highlights disabled,
  and additional spot shadows under an opaque caster.
- Per-vertex URP asset lighting and a basic Forward+ local-light loop check.
- A local reflection probe with an asymmetric cubemap, box projection off/on.
- A regular URP Unlit transparent material at queue 3000 below water, opaque-only
  fallback, selected-layer capture, and wave-normal refraction distortion.
- Both fullscreen underwater composition paths; camera/target MSAA 1/2/4/8,
  render scale 0.75, and an orthographic rendering smoke check.
- All seven shipped example scenes, exercised material-pass compilation,
  ocean-tile existence, shader compiler errors, error-shader pixel checks and
  finite HDR readback checks (including alpha) for every capture.
- Optional specular AA off/on/off captures, enabled by `-crestSpecularAA`, with
  generated high frequency normals, a narrow sun highlight and a black
  reflection environment. Checks visible highlight contribution, an image
  change when filtering is enabled, and recovery when variance is set to zero.
- Optional original Built-in/URP surface comparison on the same geometry,
  frozen simulation time, camera, material keywords, single sun and fixed cubemap.
  Foam, scene refraction, shadows, caustics, albedo and planar reflection are
  disabled for this comparison. Specular AA is disabled and
  `_NormalsStrengthOverall` is zero on both shaders: the original Built-in shader
  has neither the filter nor the corrected wave/detail normal model. The actual
  wave geometry is shared, and the comparison isolates common flat-normal lighting.
  Its mean RGB difference threshold remains **0.015**; the material's variance and
  normal strength are restored after comparison. This does not establish visual
  equivalence of the intentionally changed normal models. The user-supplied
  `90a93293` Editor report records delta 0 for this flat-normal comparison; that
  result does not cover later changes or the appearance of RNM on steep waves.

## Specular AA diagnostic

The optional diagnostic writes `11d-specular-aa-off.png`,
`11e-specular-aa-on.png`, and `11f-specular-aa-off-restored.png`. It uses a
linear mipmapped procedural normal texture and freezes Crest simulation time,
so the test does not depend on the example's normal texture or moving waves.
The test changes only a temporary water material, temporary textures and the
test sun, and restores their previous state before continuing the suite.

The enabled capture uses variance **0.15** and threshold **0.2** with a
directional highlight exponent of **4096**. This deliberately narrow highlight
exercises the filter more strongly than many ordinary water materials. The
assertion establishes that the shader consumes the controls and returns finite
pixels. A nonzero image difference alone does not establish that the filtered
image is more accurate.

To evaluate appearance, compare a short camera-motion sequence with the filter
off/on against a spatially supersampled reference. Inspect both near ripples and
distant glints for flicker, excessive broadening and brightness changes. Repeat
on the target graphics API and resolution, and measure GPU cost separately.
This runner does not yet automate that temporal or reference-image evaluation.

Image differences use linear RGB readback from a 960 × 540 ARGBHalf target:
`mean(abs(deltaR) + abs(deltaG) + abs(deltaB))` over all pixels. Saved PNGs are
gamma-converted previews; assertions use the floating-point readback.

The runner calls `SearchService.IsIndexReady(null)` in Edit Mode before entering
Play Mode to trigger default-index initialization on the pinned Unity version.
A false return value is diagnostic, not a failure or a requirement to wait for
the entire asynchronous index build. Log collection starts before pipeline and
scene initialization. Error, Exception and Assert messages are fatal regardless
of their source, including Unity Search; errors and stacks are preserved in
`SessionState` across Play Mode domain reload. Initialization exceptions also
write a failed result and exit with code 1.

The older recorded run excluded one Search error as an `EDITOR` note. That was
the old runner's behavior and is no longer its acceptance rule. Retest the new
startup handling in a disposable project with `UserSettings/Search.index` moved
to a backup name, and verify an injected Search error remains fatal across reload.
Keep checking the entire Editor/Player process log from the host: errors before
`Start`, during an unobserved reload interval, or at shutdown are outside the
managed listener's complete coverage. In particular, the supplied Android log
has a startup Java class error even though its managed rendering checks pass.

These checks do not establish full Built-in visual equivalence, all shader
variants, player stripping, performance budgets, volume/stencil edge quality,
full Forward+/Deferred behavior, D3D12, mobile, XR, dynamic resolution or arbitrary
camera-stack compatibility. Reproduce those separately for the intended game.
