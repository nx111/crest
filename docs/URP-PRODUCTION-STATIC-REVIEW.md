# Crest production source review

Date: 2026-10-11. Review baseline: `487442b37ab19151434402be65ba5fd9d6c3abee` on `urp`.

This follow-up concentrates on Crest production code. It fixes reproducible query,
simulation, shader and underwater lifecycle defects. Validation is entirely offline;
the earlier device results for `c685e51e` do not validate this new production patch.
No Unity Editor, Player, Burst worker, native graphics driver or device run was
performed for this review.

## Findings and implemented fixes

### 1. GPU query results retain their submitted layout

In [QueryBase.cs](../crest/Assets/Crest/Crest/Scripts/Collision/QueryBase.cs), the old
normal-result offset used the length of the caller's current displacement/height
output. A caller requesting only normals, or a smaller displacement output, could
therefore interpret displacement samples as the three probes for a normal.
Checking the total segment size did not distinguish point samples from normal
probes, so larger point or velocity outputs could also consume the probe region.

Each registration now retains its actual point and normal counts together with its
range and age. Those values follow the submitted readback generation. Normal
retrieval starts after the submitted point count, and every requested output is
checked against its own region before any surface output is copied. Velocity
retrieval compares submitted point counts and cannot enter the normal-probe area.
Changing only the normal channel does not unnecessarily invalidate otherwise
compatible velocity data.

The public query signatures, asynchronous readback mechanism, partial output
support and previous shutdown fixes are retained. Seven new behavioral cases fail
on the review baseline and pass with this change.

### 2. Baked FFT queries preserve coordinates and bounds between jobs

In [CollProviderBakedFFT.cs](../crest/Assets/Crest/Crest/Scripts/Collision/CollProviderBakedFFT.cs),
`Flip` copied persistent registrations and compacted their ranges without moving
their coordinates into the next writable input buffer. Queries submitted from
`FixedUpdate` could therefore read wrong positions on intervening render frames.
The old exclusive range end also acquired an extra `+ 1` during compaction.

The update sequence now completes previous work, selects the next job buffer,
rotates registrations and copies retained coordinates, then schedules new work.
Both input buffers are safe to access during the copy. The submitted quad count is
kept separately from the compacted count for the following frame, and exclusive
range ends remain exclusive.

Each owner also keeps its exact point count, excluding unused SIMD lanes. All
requested result channels and all required registration capacities are checked
before output arrays or registrations are changed. Capacity failures now reach the
public API as `TooManyQueries`. Cleanup is idempotent; calls after cleanup return
unavailable data without accessing disposed arrays.

The controlled affine sampler demonstrates the previous errors independently of
FFT mathematics: a retained height expected to be `48` became `8`; after owner
expiry and compaction, an expected `148` became `548`. At the exact 8192-quad
capacity, the following old batch attempted to schedule 8193 quads. These cases,
cross-owner and padding-lane output bounds, missing channels and disposal now pass.

Copying coordinates adds necessary CPU memory traffic; the existing two input
buffers are reused. No performance improvement is claimed for this provider.
The separate FFT sampling-equation checks still pass.

### 3. Sampling helpers report unavailable velocity correctly

The two velocity-producing overloads in
[SamplingHelpers.cs](../crest/Assets/Crest/Crest/Scripts/Collision/SamplingHelpers.cs)
previously used only `RetrieveSucceeded`. A GPU query can have valid displacement
and normal data while its velocity is invalid because history is missing,
registration changed, or the timestamps do not form a usable difference. The
helper could return `true` with a velocity retained from an older query.

For a `QueryBase` provider, the helper now checks those velocity status flags. If
only velocity is unavailable, it returns `false` and zero velocity while preserving
the valid height/displacement and normal. This keeps surface information useful to
existing callers that inspect the output values independently of the boolean.
The XML comments document this behavior. Non-GPU providers retain their own opaque
status semantics; their numeric status values are not interpreted as GPU flags.
Height-only and height/normal overloads are unchanged.

The checks execute the actual helper and query code, first obtain nonzero velocity,
then produce same-time results. Both old helper overloads incorrectly accept the
stale velocity; both corrected overloads reject it. An independent provider with an
opaque nonzero success status continues to work.

### 4. FFT generation validates resources before reusing a timestamp

[FFTCompute.cs](../crest/Assets/Crest/Crest/Scripts/Shapes/FFT/FFTCompute.cs) previously
returned its cached wave texture before checking whether resources still existed.
After release, the same simulation time could return a null output; a later time
could allocate new textures while incorrectly retaining the old spectrum-initialized
flag. The initial timestamp sentinel `-1` was also a valid custom-clock value.

The cache now requires valid control textures and all eight created render
textures. Invalid resources are rebuilt before considering a timestamp hit.
Release clears the spectrum state and uses `NaN` for the unset timestamp, so any
finite initial time, including `-1`, generates data. Valid same-time calls still
share one generated texture without redundant dispatches; explicit spectrum
updates still bypass that cache.

The resource substitutes cover release followed by equal/later times, a paused
`CleanUpAll`, changed wave conditions, loss of each render texture, and destruction
of either control texture. This verifies dispatch and cache decisions, not the
GPU contents themselves. Unity's
[IsCreated contract](https://docs.unity3d.com/cn/6000.0/ScriptReference/RenderTexture.IsCreated.html)
distinguishes a texture object from its created hardware resources; the cache must
not rely on a non-null reference alone.

### 5. Zero-time dynamic-wave passes only transport history

The persistent simulation intentionally performs a zero-time pass on frames with
no simulation step. In
[UpdateDynWaves.compute](../crest/Assets/Crest/Crest/Shaders/Resources/UpdateDynWaves.compute),
the old kernel still evaluated a Courant division by zero and applied its edge
damping to height and velocity. Extra render frames could therefore drain wave
energy without advancing simulation time.

After the existing out-of-cascade and dry-land checks, the kernel now samples the
reprojected history and returns it directly for a nonpositive step, rejecting
nonfinite history. Positive steps retain the existing wave propagation, edge
damping and shallow attenuation arithmetic.

In the controlled edge fixture, a height of `1.25` became `1.207031` after one
zero-time pass, and approximately `0.018790` after 120 such passes. The corrected
kernel retains `1.25`. The test compiles the actual kernel arithmetic and coordinate
helpers with CPU vector/texture adapters; it does not emulate hardware filtering
or half-precision storage. Positive-step controls also pass.

### 6. Flow phases stay bounded at negative time

Custom clocks and cutscene/network time offsets can supply negative `_CrestTime`.
The former signed `fmod` produced negative blend weights in normal mapping, foam
and both wave-combine implementations. With time `-0.5` and period `2`, the weights
were `(-0.5, 1.5)`. Passing valid opaque-white and transparent-black samples through
the actual foam blend produced coverage `-0.5` and RGB `-50000000`, which overflows
a half-precision output.

[OceanHelpersNew.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanHelpersNew.hlsl) now
provides `CrestFlowPhase`, which adds the positive period only for a negative
remainder. Its existing include sites are used by
[OceanNormalMapping.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanNormalMapping.hlsl),
[OceanFoam.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanFoam.hlsl), and the
[compute](../crest/Assets/Crest/Crest/Shaders/Resources/ShapeCombine.compute) and
[fragment](../crest/Assets/Crest/Crest/Shaders/Resources/ShapeCombine.shader)
combine paths. Positive-time results and the normal path's existing centered
offsets are retained.

The checks execute all four production phase formulas and the actual foam blend.
They cover bounded weights/coverage, positive-time compatibility, negative and
positive periods, and continuity across both phase resets. Existing detail-normal,
foam shading and foam-lighting interpreters now evaluate the shared source helper
and include negative-time controls.

### 7. Horizon classification keeps absolute height in float

[UnderwaterMaskHorizonShared.hlsl](../crest/Assets/Crest/Crest/Shaders/Underwater/UnderwaterMaskHorizonShared.hlsl)
cast reconstructed world height to `half4` before comparing it with the float sea
level. On a path using native half precision, sea level `4096` and point height
`4097` both compare as `4096`, classifying the above-water point as underwater.
Negative elevations and heights beyond the finite half range also have
counterexamples.

The comparison now stays in float; only the final small mask constants use the
existing `half4` output. Source-driven binary32/binary16 checks cover raised and
lowered oceans, large absolute heights and the exact-sea-plane convention. This
proves the precision defect for that arithmetic path, not that every device
previously displayed it.

### 8. Underwater primary-camera state follows component changes

[UnderwaterRenderer.cs](../crest/Assets/Crest/Crest/Scripts/Underwater/UnderwaterRenderer.cs)
previously updated `Instance` only after a successful component lookup on a changed
primary camera. Switching to a camera without an underwater component left the
old instance associated with the new primary camera. `Get(newCamera)` could then
return the old camera's renderer. A disabled component could be selected, while a
new component on the same camera could be missed.

The update now resolves both camera and active component, including missing ocean,
missing camera and unchanged-camera component transitions. Editor callbacks are
removed while the old instance is still current, and added only after assigning
the new instance. Unchanged valid frames do not repeat the subscriptions.
Secondary-camera lookup retains its existing behavior.

### 9. Underwater destruction releases owned native objects

Disabling the component releases mask hardware allocations for later reuse, but
the old class had no final destruction of its three created materials, four render
texture objects or two command buffers. A new `OnDestroy` releases the RTHandle
wrappers, releases command buffers and destroys the owned objects. Cleanup tolerates
partial initialization and repeat invocation. Shared ocean material and loaded
compute-shader assets are preserved; final destruction does not repeat global
mask-keyword teardown for other cameras.

The ownership review used the pinned URP 17.6 Core sources:
`Runtime/Textures/RTHandles.cs`, `RTHandleSystem.cs` and `RTHandle.cs`.
`Alloc(RenderTexture, transferOwnership = false)` creates non-owning wrappers;
their `Release` does not destroy the renderer's textures. The selected-method
checks enforce wrapper-before-texture destruction, disable/reuse behavior and
Editor subscription ordering. They do not measure native object counts or VRAM.

## Offline verification

Each previous-revision run uses the current test logic with production sources
read from Git, without modifying the working checkout. A failure in the baseline
column is an intentional counterexample, not a passing build gate.

| Check | Modified source | Baseline `487442b3` with the same cases |
| --- | ---: | --- |
| Production GPU query and sampling helpers | 34/34 pass | 25/34 pass; seven layout and two stale-velocity failures |
| Production baked query provider | 11/11 pass | 1/11 pass; ten failures |
| Production FFT resource/cache lifecycle | 17/17 pass | 2/17 pass; fifteen failures |
| Production dynamic-wave kernel with CPU adapters | 9/9 pass | 6/9 pass; three zero-time failures |
| Selected underwater lifecycle methods, Player | 11/11 pass | 2/11 pass; nine failures |
| Selected underwater lifecycle methods, Editor | 11/11 pass | 2/11 pass; nine failures |
| Four Flow paths and foam bounds | 5/5 methods pass | Four methods fail across 325 numerical subcases; positive-time compatibility passes |
| Optical equations and horizon classification | 12/12 methods pass | Two methods fail across 20 numerical subcases |
| Existing foam shading / lighting | 10/10 and 12/12 pass | Extended negative-time continuity cases reproduce the old fault |
| Existing normal sampling / detail normals | 6/6 and 10/10 pass | Includes new negative-time controls |
| Existing FFT sampling equations / URP regressions | 13/13 and 9/9 pass | Separate compatibility checks |
| C# syntax, Editor/Player for Unity 6.0/6.6 defines | 147 files × 4 configurations, zero errors | Not a Unity API-binding check |
| DXC with Unity 6000.6.2f1 and URP 17.6 headers | 250/250 selected stages/variants pass | Not a complete Unity variant/import/build check |

The DXC matrix now includes the dynamic-wave kernel, every declared combine
compute kernel with its actual per-kernel defines, and fragment-combine Flow and
dynamic-wave combinations. It also checks the affected ocean and horizon consumers.
It targets the existing D3D shader compilation path; this does not establish native
half behavior, Vulkan execution, Render Graph scheduling or image quality.

### Reproduce

From the repository root, with a .NET 10 SDK and Python 3 available:

```sh
python3 tools/check_query_lifecycle.py --dotnet /path/to/dotnet
python3 tools/check_baked_query_lifecycle.py --dotnet /path/to/dotnet
python3 tools/check_fft_lifecycle.py --dotnet /path/to/dotnet
python3 tools/check_underwater_lifecycle.py --dotnet /path/to/dotnet
python3 tools/check_dynamic_waves.py
python3 tools/check_flow_sampling.py
python3 tools/check_optical_quality.py
python3 tools/check_foam_shading.py
python3 tools/check_foam_lighting.py
python3 tools/check_normal_sampling.py
python3 tools/check_detail_normals.py
python3 tools/check_fft_queries.py
python3 tools/check_urp_regressions.py
```

`check_dynamic_waves.py` also requires a C++17 compiler. Add `--ref 487442b3` to
the relevant regression command to run the previous production source. These
managed projects clear NuGet sources and use no downloaded packages.

```sh
/path/to/dotnet run --project tools/StaticChecks -- crest/Assets
python3 tools/check_urp_shaders.py \
  --dxc /path/to/dxc \
  --urp-headers /path/to/urp17.6 \
  --unity-includes /path/to/unity-6000.6.2f1/CGIncludes \
  --output /path/to/crest-dxc-results
git diff --check
```

## Remaining work and limits

This patch corrects numerical and ownership defects; it does not establish a new
device frame budget or a measured improvement in final rendered appearance.
The previous RHalf foam lifetime drift and Android frame-budget findings remain
open. Artistic changes to scattering, foam amount, reflection roughness and scene
quality settings are outside this static pass.

The separate `FFTCompute.OnGenerationDataUpdated` ownership candidate identified
here is addressed in the subsequent
[simulation and resource lifecycle review](URP-SIMULATION-LIFECYCLE-REVIEW.md).
That continuation retains ownership when conditions merge, protects outputs already
used in the current frame, reuses compatible idle storage and releases the cache
at final cleanup. It also fixes condition-key collisions. Its new offline results
are recorded separately and do not change the device-validation limits above.

No exhaustive claim is made about every Crest code path. The fixes and checks
above are bounded, reproducible source findings, with device validation deferred.
