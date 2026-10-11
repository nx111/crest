# Unity rendering quality checks

`QualityRunner.cs` exercises the actual ocean shaders, Render Graph passes and
camera rendering on a GPU. It targets Unity **6000.6.2f1 / URP 17.6.0**, the pinned
versions of the example project. Results recorded on 2026-10-11 are in
[URP-QUALITY-AUDIT.md](../../docs/URP-QUALITY-AUDIT.md).

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
    '-executeMethod', 'QualityRunner.Start', '-logFile', $qualityLog
) -WindowStyle Hidden -PassThru
$qualityProcess.Id
```

The initial import may take several minutes. Do **not** add `-batchmode`,
`-nographics` or `-quit`: the test enters Play Mode to create Crest's ocean tiles,
needs a graphics device, and exits itself after the captures. A successful run
prints `CREST_QUALITY_RESULT PASS`, writes `QualityCaptures/results.txt` and PNGs,
and exits with code 0. A failed assertion or rendering exception exits with code 1.

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
  ocean-tile existence, shader compiler errors and error-shader pixel checks.
- Optional original Built-in/URP surface comparison on the same geometry,
  frozen simulation time, camera, material keywords, single sun and fixed cubemap.
  Foam, scene refraction, shadows, caustics, albedo and planar reflection are
  disabled for this comparison. This isolates surface shading rather than
  comparing two complete projects.

Image differences use linear RGB readback from a 960 × 540 ARGBHalf target:
`mean(abs(deltaR) + abs(deltaG) + abs(deltaB))` over all pixels. Saved PNGs are
gamma-converted previews; assertions use the floating-point readback.

The runner reports Unity Search indexing exceptions separately as `EDITOR`
notes when their stack is in `UnityEditor.Search.SearchDatabase`. Actual runtime,
shader and Render Graph errors remain fatal. The recorded final run has one
such indexing note and no rendering errors.

These checks do not establish full Built-in visual equivalence, all shader
variants, player stripping, performance budgets, volume/stencil edge quality,
full Forward+/Deferred behavior, D3D12, mobile, XR, dynamic resolution or arbitrary
camera-stack compatibility. Reproduce those separately for the intended game.
