# Unity rendering quality checks

`QualityRunner.cs` and its optional audit modules exercise the actual ocean shaders, Render Graph passes and
camera rendering on a GPU. It targets Unity **6000.6.2f1 / URP 17.6.0**, the pinned
versions of the example project. Earlier GPU results recorded on 2026-10-11 are
in [URP-QUALITY-AUDIT.md](../../docs/URP-QUALITY-AUDIT.md).
The [latest retest](../../docs/URP-UNITY-RUNTIME-RETEST-9df1db9f.md) covers production
commit `c685e51e`: 376 Editor assertions, 129 per Windows graphics API and 182 on
Android Vulkan, including actual main-screen and native shutdown evidence. The
expanded Editor/Player sources are now in this directory; exact tested versions
are archived alongside the report. The [independent review](../../docs/URP-RUNTIME-REVIEW-c685e51e.md)
tightens process/log acceptance, production velocity checks and actual foam-format
diagnostics. These later diagnostic edits have offline checks and need a new
Unity/device run; they do not increase the archived GPU assertion counts.

The [document review follow-up](../../docs/URP-DOCUMENT-REVIEW-FOLLOWUP.md) changes
the production half-precision foam store. The Editor and Player foam fixtures now
set the rounding flag from the allocated graphics format and advance the seed for
every real simulation dispatch. Lifetime checks use the interior spatial mean,
with a margin derived from half ULP variance and the existing float32 budget.
These updated fixtures have syntax/offline checks only and need a new GPU run.
`check_foam_storage.py --results` accepts historical uncorrected scalar logs; it
rejects the new corrected spatial-mean logs.

`-crestRealismAudit` enables `RealismAudit.cs`: selected RNM and per-phase foam HLSL
contracts, FFT coordinate/time and Burst comparisons, production FFT loop/bake
checks, foam-decay characterization and planar-reflection mip/resource recovery.
The Player modules provide corresponding bounded HLSL/query checks plus optional
device diagnostics. A finite readback or an image difference does not establish
natural appearance or temporal stability.

Remaining visual work includes elevated shallow-water/EXR checks, long-running
Gerstner precision, nonuniform curved-flow transport and emitter-frequency sweeps,
foam LOD/phase coverage, moving-camera glints and longer CPU/GPU profiling. See the
[reflection](../../docs/URP-REFLECTIONS-AND-SHALLOW-WATER.md#6-本地画面与性能验收),
[wave and foam](../../docs/URP-WAVES-AND-FOAM-STABILITY.md#6-本地画面与性能验收), and
[surface detail](../../docs/URP-SURFACE-DETAIL-AND-WAVE-QUERIES.md#6-本地画面与性能验收)
reports for scenarios. Re-bake existing FFT collision assets after the corrected
wave spectrum/timing changes. The Built-in comparison below isolates common
lighting with flat shading normals; it does not validate the new normal model's appearance.

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
foreach ($directory in @('Assets/Editor', 'Assets/Resources')) {
    New-Item -ItemType Directory -Path (Join-Path $qualityProject $directory) -Force | Out-Null
}
foreach ($file in @('QualityRunner.cs', 'RealismAudit.cs', 'RealismAudit.shader',
                    'PlayerAuditBuilder.cs', 'AndroidAuditBuilder.cs', 'RunnerStartupProbe.cs')) {
    Copy-Item -LiteralPath (Join-Path 'tools/UnityQualityTests' $file) -Destination (Join-Path $qualityProject 'Assets/Editor')
}
foreach ($file in @('PlayerAudit.cs', 'PlayerRetest.cs', 'PlayerGpuContracts.cs',
                    'RuntimeQueryAudit.cs', 'PlayerTemporalAudit.cs')) {
    Copy-Item -LiteralPath (Join-Path 'tools/UnityQualityTests' $file) -Destination (Join-Path $qualityProject 'Assets')
}
foreach ($file in @('RealismAudit.shader', 'AuditQueryCoordinates.compute')) {
    Copy-Item -LiteralPath (Join-Path 'tools/UnityQualityTests' $file) -Destination (Join-Path $qualityProject 'Assets/Resources')
}

# Optional original Built-in surface comparison; requires the baseline Git commit.
python tools/UnityQualityTests/prepare_builtin_reference.py $qualityProject
# Required by the optional -crestRealismAudit same-geometry detail comparison.
python tools/UnityQualityTests/prepare_realism_reference.py $qualityProject

# Replace this editor path with your Unity 6000.6.2f1 installation.
$unityEditor = 'E:/Games/Unity/Editor/6000.6.2f1/Editor/Unity.exe'
$qualityLog = Join-Path $qualityProject 'quality-gpu.log'
$qualityProcess = Start-Process -FilePath $unityEditor -ArgumentList @(
    '-force-d3d11', '-projectPath', $qualityProject,
    '-crestSpecularAA', # Optional diagnostic; remove to skip its extra captures.
    '-crestRealismAudit', '-crestQueryAudit', '-crestQuitAudit', '-crestLeakStacks',
    '-executeMethod', 'QualityRunner.Start', '-logFile', $qualityLog
) -WindowStyle Normal -PassThru
$qualityProcess.Id
```

The initial import may take several minutes. Do **not** add `-batchmode`,
`-nographics` or `-quit`: the test enters Play Mode to create Crest's ocean tiles,
needs a graphics device, and exits itself after the captures. A successful run
prints `CREST_QUALITY_RESULT PASS`, writes `QualityCaptures/results.txt` and PNGs,
and exits with code 0. A failed regular assertion or rendering exception exits with code 1.
The runner installs its log listener before opening scenes, defers that initialization
until the editor UI is ready, and preserves startup failures across the Play Mode
domain reload. Inspect the complete process log after exit as well as the results
file: native shutdown leaks can occur after the managed test reports PASS. With
`-crestQuitAudit`, require the final successful shutdown assertion in `results.txt`
and `CREST_QUERY_QUIT_AUDIT PASS` without FAIL in the complete process log. A managed
PASS or exit code 0 alone is insufficient because the quit probe runs later.

`prepare_runtime_retest.py --label <new-label> <cached-project>` can update a stopped,
existing `crest-validation-*` diagnostic copy with a Library cache; it is not the
fresh-project setup command. Preserve each run's results and installed tool snapshot.

For the native query and shutdown audit, copy `RuntimeQueryAudit.cs` outside Editor
and `AuditQueryCoordinates.compute` into `Assets/Resources` in the disposable copy
(included in the complete setup above).
Add `-crestQueryOnly -crestQuitAudit -crestLeakStacks` to run just these checks.
The shutdown audit deliberately leaves a live provider and an already-cleaned
provider with pending native requests, then verifies their resources in the quit
event. Full quality checks remain the default when `-crestQueryOnly` is absent.

The Player audit uses `PlayerAudit.cs`, `PlayerRetest.cs`, `PlayerGpuContracts.cs`,
`RuntimeQueryAudit.cs` and `PlayerTemporalAudit.cs` outside Editor; install its
builders in `Assets/Editor` and the diagnostic shader/compute in `Assets/Resources`.
Use `-crestQueryAudit -crestQuitAudit -crestFoamStorage -crestPerformanceAB` for
the extended diagnostics. Android `run_android_audit.py` accepts these via `--extra`,
checks the app's own logcat including native teardown, and reconnects a dropped
Wi-Fi ADB endpoint. Keep `android_audit_checks.py` beside both Android host scripts.
The host requires confirmed process exit, a successfully retrieved final file with
exactly one `RESULT: PASS`, no failed assertions, and a retained early Unity
`Context Type: ActivityOrService` startup line. This marker precedes the known Java
class probe; later engine-initialization lines alone cannot prove startup coverage.
Requested quit checks must also appear in both the final file and PID log. Timeouts,
unverifiable ADB replies, missing/truncated evidence or a replaced app PID fail
acceptance. Errors are saved to `acceptance-errors.txt`; `full-log-errors.txt` retains
the log-specific errors. `-crestBackgroundAudit` exercises Home/resume on that test app.
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
R32 cost comparison is explicitly skipped. The separate 32x32 compute decay test
uses uniform input and zero Flow, but still executes the production linear sampler.
It bypasses the manager's compatibility selection and does not establish support
for nonuniform filtering or the full ocean's R32 texture. It now checks creation
and actual `graphicsFormat` of both ping-pong targets; a fallback explicitly skips
that requested storage label. Only verified `R32_SFloat` targets use the R32 error
threshold. The archived Android run predates these small-target format checks.

The production wave-query audit resets displacement/normal/velocity outputs to
NaN before each call and requires `QueryStatus.OK`. `RetrieveSucceeded` alone
does not reject unavailable or invalid velocity data; untouched zero outputs must
not pass as valid velocities.

`MinimalAndroidBuilder.cs`, `MinimalAndroidLifetime.cs` and `run_minimal_android.py`
provide a separate empty-project Android startup control, without Crest or URP.
The example project's `Development/Editor/AndroidAssetPackDependency.cs` works
around the confirmed Unity 6000.6.2f1 AssetPackManager startup error by adding the
official Play Asset Delivery 2.3.0 dependency if it is absent. It applies only to
that engine version; keep it in the project when building the Android example.
An existing game with Play/PAD plugins needs its final Gradle dependency graph
checked before adopting this example-specific hook: it detects a directly declared
asset-delivery dependency, not every transitive/local AAR or old monolithic Play Core
library. Follow [Google's migration instructions](https://developer.android.com/guide/playcore#common-migration-steps)
when old Play Core libraries are present.

Host acceptance regressions can run without a phone or Unity:

```text
python -m unittest discover -s tools/UnityQualityTests -p test_android_audit_checks.py -v
```

They exercise the real CLI scripts with fake ADB replies, exit/transport/result/log
failure cases, and content replay of archived positive/negative Android logs. This
does not constitute a new device run or independently observe archived process exit.

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
managed listener's complete coverage. The original Android baseline had a startup
Java class error despite managed rendering PASS; the final `c685e51e` device log
includes early startup and shutdown with no recorded errors after the dependency fix.

These checks do not establish full Built-in visual equivalence, all shader
variants, player stripping, performance budgets, volume/stencil edge quality,
full Forward+/Deferred behavior, every D3D12/mobile path, XR, dynamic resolution or
arbitrary camera-stack compatibility. The report covers specific D3D11/D3D12/Vulkan
cases; reproduce additional configurations separately for the intended game.
