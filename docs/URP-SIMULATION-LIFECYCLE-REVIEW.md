# Crest simulation and resource lifecycle review

Date: 2026-10-12 (UTC+08; 2026-10-11 UTC). Review baseline: `7a710032ac2d14c1e409954068a536489a124120` on `urp`.

This continuation fixes source-level defects in Crest's wave generation, input
ordering, simulation history and owned rendering resources. It follows the
[previous production review](URP-PRODUCTION-STATIC-REVIEW.md). This batch changes
C# production code and adds offline behavioral checks; it does not change shaders,
material defaults or example quality settings. No Unity Editor, Player, native GPU
or device test was performed. Earlier device reports do not validate these changes.

## Findings and implemented fixes

### 1. FFT conditions have an identity, not just an integer hash

[FFTCompute.cs](../crest/Assets/Crest/Crest/Scripts/Shapes/FFT/FFTCompute.cs) previously
used an integer from `Hashy` as the entire dictionary key. The production `Hashy`
combines values with XOR, so exchanging two float parameters leaves the result
unchanged. For example, wind speed `0.75` with turbulence `0.2` and wind speed `0.2`
with turbulence `0.75` collide with all other inputs equal. Both conditions are
within the supported parameter ranges. At the same timestamp, the second request
then returns the first condition's already generated texture without dispatching
its own spectrum.

The cache now uses a value-type `WaveConditions` key containing resolution, loop
period, turbulence, direction, speed and spectrum reference. Equality checks all
six inputs; a dictionary hash collision can no longer make different conditions
equal. Float equality and hashing are paired, and spectrum comparison/hash use
managed reference identity so Unity's destroyed-object null behavior does not
change a stored key. Identical requests continue to share a generated output.

The FFT harness now compiles the actual production `Hashy.cs` along with
`FFTCompute.cs`, including for old Git revisions. Its previous substitute used a
different hash formula and would have hidden this specific production collision.
This change makes the old-version counterexample exercise the actual faulty key.

### 2. Shared FFT outputs survive condition changes within a frame

The animated-wave manager updates all registered shapes before drawing their wave
inputs. An earlier shape can therefore already reference a generator's output in
its material when a later shape changes conditions. The old implementation could
repurpose that same generator and overwrite its texture before either shape drew.
Both shapes would then sample the later condition's waves.

Generators now record their last requesting `OceanRenderer.FrameCount`, including
same-timestamp cache hits. A condition change can repurpose only a generator not
used in the current logical frame. This also protects paused/custom clocks, where
simulation time alone cannot identify the frame. Unchanged-condition notifications
are no-ops.

When the destination condition already exists, the old cache entry retains
ownership instead of being dropped. Its textures may still be referenced by other
shapes or already recorded commands. When a new condition needs storage, the
existing old-condition generator is preferred if idle; otherwise an idle generator
of the same resolution and loop period can be reused after removing its old key.
The new spectrum is marked dirty before generation.

This spare-storage path matters for continuously changing weather: two shapes
where the first adopts the second's previous condition and the second moves on
would otherwise accumulate a texture set every frame. The controlled 120-frame
sequence now retains two texture sets and two independent outputs at every frame.
All outputs already used in a frame remain ineligible for reuse, and incompatible
resolution/period caches remain untouched.

`CleanUpAll` releases every cached generator and clears the dictionary. Subsystem
registration performs that cleanup before resetting the cache instead of simply
discarding its owners. Each initialized generator owns eight render textures and
two control textures. Loaded compute-shader assets remain shared.

This is an ownership and reuse policy, not a global VRAM budget or an LRU eviction
system. Unused high-water allocations can remain cached until final FFT-family
cleanup. Changing resolution/loop-period buckets or generating conditions outside
the shape notification path is not given a new capacity limit. The key covers the
six explicit generation arguments; separate environment inputs such as runtime
`OceanRenderer.Gravity` changes are not newly tracked by this patch.

### 3. Fixed-step rounding cannot suppress the pause-frame update

[LodDataMgrPersistent.cs](../crest/Assets/Crest/Crest/Scripts/LodData/LodDataMgrPersistent.cs)
accumulates elapsed time, executes fixed simulation substeps, and transports history
on frames with no substep. Repeated binary32 subtraction can leave a small negative
remainder after an exact batch: `0.1` seconds at 30 Hz leaves approximately
`-7.45058e-9`, while `0.05` seconds at 60 Hz leaves approximately `-3.72529e-9`.

On the next paused frame, flooring that negative remainder produces `-1` substeps.
The old special case only converted exactly zero to a history-transport pass. It
could flip the published buffers without dispatching an update, exposing an older
target, with `LastUpdateSubstepCount` incorrectly equal to `-1`.

The remainder is now clamped to zero after each consumed substep, and the requested
substep count is clamped nonnegative before the existing zero-time transport logic.
Foam and dynamic waves therefore continue to publish the last completed simulation
after a pause. Positive-step arithmetic, normal ping-pong order and accumulation
of intervals below one fixed step retain their existing behavior. This fixes CPU
dispatch decisions; it is separate from the earlier zero-time dynamic-wave shader
fix and does not resolve half-precision foam lifetime drift.

### 4. Lost simulation textures trigger a complete, bounded recovery

Previously, [LodDataMgr.cs](../crest/Assets/Crest/Crest/Scripts/LodData/LodDataMgr.cs)
checked resolution changes but did not recognize a lost hardware allocation at the
same settings. A non-null `RenderTexture` is not proof that its hardware resource
exists, as documented by Unity's
[IsCreated contract](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/RenderTexture.IsCreated.html).
For example, Flow with no inputs can retain its `_targetsClear` flag and skip
further work even after its previously cleared output loses its allocation.

The base manager now checks all buffered output textures.
[Persistent simulations](../crest/Assets/Crest/Crest/Scripts/LodData/LodDataMgrPersistent.cs)
also check their additional source texture, and
[animated waves](../crest/Assets/Crest/Crest/Scripts/LodData/LodDataMgrAnimWaves.cs)
check the wave and combine intermediate textures. These checks use existing
buffers without allocating a collection per frame.

[OceanRenderer.cs](../crest/Assets/Crest/Crest/Scripts/OceanRenderer.cs) uses its
existing full rebuild path if any enabled simulation loses a required texture,
even when the settings hash is unchanged. Rebuilding the managers together resets
the persistent source/target history, prewarm state, cached clear flags and shared
cascade buffers coherently. Recreating only one history texture would leave
undefined data associated with otherwise valid old state.

After the single rebuild attempt, the renderer verifies initialization, component
enable state and texture validity before continuing. A persistent allocation
failure reports the affected simulation and disables the ocean; it does not start
an endless per-frame rebuild cycle or proceed with subsequent simulation updates.
Newly enabled simulations also pass through a validity gate before use. Manager
initialization can already have attempted texture creation/clearing at this point;
the check does not claim to prevent every initialization command or to reproduce a
native GPU allocation failure.

Rebuilds preserve the existing runtime time-provider stack throughout cleanup and
initialization. This is necessary for a dynamically pushed cutscene or network
clock: resetting the stack to the default clock would otherwise silently remove
an external provider that does not re-register when the ocean rebuilds. The normal
initial `OnEnable` setup is retained, and the rebuild guard is restored in a
`finally` block.

Finally, [BuildCommandBuffer.cs](../crest/Assets/Crest/Crest/Scripts/BuildCommandBuffer.cs)
now releases its owned command buffer during ocean cleanup. Previously each full
rebuild replaced the builder without a final release of that native object.
Cleanup is idempotent; ordinary disable/enable paths that retain ocean resources
continue to retain the builder.

Recovery intentionally restarts transient simulation history; lost foam or ripple
contents cannot be reconstructed from a missing GPU allocation. A full rebuild can
also have a one-frame cost. The continuous validity checks and recovery cost have
not been profiled on a device.

### 5. The first history frame contains valid cascade parameters

New shared cascade buffers start at zero. The old first frame wrote only the
current cascade parameters, then uploaded the still-zero previous parameters as
the simulation source. `WorldToUV` divides by `texelWidth * textureRes`; for those
uninitialized source cascades the denominator is zero. This source-level defect
exists at initial startup and recurs after a rebuild, independently of whether
the simulation textures themselves were cleared.

The first complete per-frame write now copies current cascade parameters and
per-cascade instance data into every history slot before uploading the buffers.
The full arrays are copied, including the valid off-cascade sentinel. Subsequent
frames preserve actual previous data instead of repeatedly overwriting history.
The initialization flag resets when buffers are newly created, and retained
resources do not lose their history during an ordinary disable/enable.

This removes the demonstrated zero-denominator source path. It is not a claim
about a particular driver's handling of NaN UVs or about an observed first-frame
image artifact.

### 6. Wave families and generated geometry keep explicit ownership

[ShapeWaves.cs](../crest/Assets/Crest/Crest/Scripts/Shapes/ShapeWaves.cs) previously
counted all derived wave types together and called only the last component's
`DestroySharedResources`. In a mixed FFT/Gerstner scene, destroying the last FFT
before the last Gerstner could skip FFT cleanup; reversing the order could skip
the shared swell-spectrum cleanup.

Separate resource-family counts now let the last
[FFT](../crest/Assets/Crest/Crest/Scripts/Shapes/FFT/ShapeFFT.cs) and last
[Gerstner](../crest/Assets/Crest/Crest/Scripts/Shapes/ShapeGerstner.cs) each clean up
their own shared resources, including subclasses of either family. The common
default wind spectrum remains alive until the last wave component of any family
is destroyed.

Components also destroy their generated global/spline materials and owned spline
mesh on final destruction. The spline mesh has a separate owning field; renderer
`sharedMesh` and `sharedMaterial` references remain borrowed. Previously, adding a
spline after selecting a renderer could pass the borrowed shared mesh into the
spline generator and mutate that external asset.

Invalid/removed splines release their generated mesh and refresh registrations.
An invalid spline does not submit a full-screen draw or fall back to a borrowed
renderer mesh with spline geometry settings. A deferred generation result, such
as the generator's Editor Undo early return, is tolerated even before any mesh has
been created; an existing owned mesh remains reusable. Geometry generation itself
is substituted in the offline ownership fixture, so these checks establish
ownership/control flow rather than the correctness of generated triangles.

### 7. Global-wave input order survives integer boundaries

[RegisterLodDataInput.cs](../crest/Assets/Crest/Crest/Scripts/LodData/RegisterLodDataInput.cs)
multiplied an integer queue by 1000 to reserve sibling-order slots. Global waves
use `int.MinValue` as their priority sentinel; that multiplication wraps to zero.
Consequently a local blend at queue zero could run before a global additive input,
changing the final composed waves. Extreme custom positive/negative queues could
also wrap across the order.

The public integer registrar is retained. The implementation reserves the lowest
1000 keys for the global sentinel, calculates ordinary queues in 64 bits, clamps
them above that reserved range and below the upper integer boundary, then adds a
clamped sibling index. Ordinary nonconflicting queue and sibling order is retained.
Extreme queues can saturate to the same final key; their relative tie order is not
promised. This patch does not replace `CrestSortedList` or introduce a new generic
sorting algorithm.

### 8. Floating-origin compensation reaches each receiver once

[ShiftingOrigin.cs](../crest/Assets/Crest/Crest/Scripts/Helpers/ShiftingOrigin.cs)
first notified ocean-child `IShiftingOrigin` components and then separately
notified legacy Gerstner components. A `ShapeGerstnerBatched` under the ocean belongs
to both groups and therefore received the same phase compensation twice.

One receiver set now deduplicates the two traversals and duplicate override entries;
null Gerstner override entries are skipped. External Gerstner components still
receive compensation, and subsequent independent origin shifts notify every
receiver again. In the controlled phase fixture, the expected cosine after one
shift is approximately `-0.21713525`; the old double compensation gives
approximately `-0.8009317`. The corrected actual `SetOrigin` method preserves the
expected phase continuity. This allocation occurs on origin shifts, not every
normal simulation frame.

### 9. Volume-mode switches configure the required back-face depth target

[UnderwaterRenderer.Mask.cs](../crest/Assets/Crest/Crest/Scripts/Underwater/UnderwaterRenderer.Mask.cs)
previously returned from `SetUpVolumeTextures` when the front target matched the
camera descriptor, skipping the back target entirely. Portal mode only configures
the front. A same-size switch from Portal to Volume or VolumeFlyThrough could
therefore leave the newly required back target as a `0 x 0` placeholder. A resize
during Portal could instead leave the back target at an older size or array/dynamic
resolution configuration.

Front and back descriptors are now checked independently after normalizing the
existing single-sample depth format. Portal still leaves its unused back target
alone. Matching targets retain their allocation and object identity; mode switches
do not replace the render textures or their RTHandle wrappers. Both initial mode
switches and later descriptor transitions have old-version counterexamples.

## Offline verification

The current test logic is also run against production source read from baseline
`7a710032`, without changing the checkout. Baseline failures are intentional
negative controls. Managed substitutes model resource ownership, recorded commands
and scene lookup; they do not execute Unity native APIs, GPU work or real spline
generation.

| Check | Modified source | Baseline `7a710032`, same cases |
| --- | ---: | ---: |
| FFT key, shared output, storage reuse and cleanup | 29/29 pass | 21/29 pass; 8 failures |
| Persistent simulation substep/history control | 7/7 pass | 2/7 pass; 5 failures |
| LOD resource recovery, shared history, clocks and builder | 23/23 pass | 6/23 pass; 17 failures |
| Wave/input ownership, ordering and origin compensation | 19/19 pass | 3/19 pass; 16 failures |
| Underwater Volume target transitions | 6/6 pass | 2/6 pass; 4 failures |
| **Behavioral total** | **84/84 pass** | **34/84 pass; 50 intentional failures** |
| C# syntax, Editor/Player under Unity 6.0/6.6 defines | 147 files x 4 configurations; zero errors | Syntax only, not Unity API binding |

The resource fixture compiles six complete manager classes and the complete
`BuildCommandBuffer`. It extracts Ocean update/rebuild/cleanup/publication methods
and `LodTransform.WriteCascadeParams` verbatim. The actual `OnEnable` clock and
retained-resource prefix runs before a substituted scene/resource setup, and its
fresh-history reset is copied from the production source. Thus it verifies those
decisions with actual manager allocations and recorded operations in the managed
model; it does not execute full Unity scene construction or Editor/native
callbacks. Rebuild clock preservation is tested directly, independently of the
new lost-texture detection gate. The ordinary next-frame and enable/reuse cases
serve as compatibility controls.

The input fixture compiles the complete base wave/input/origin classes and uses
the unchanged production derived-family cleanup and legacy Gerstner phase method.
The Volume fixture extracts the actual target setup and descriptor comparison
methods. Both distinguish component-owned resources from borrowed references.

No shader source changed in this batch. The previous review's 250 selected DXC
stages/variants are not presented as a new compilation result or as Unity/GPU
validation of the C# lifecycle changes. `git diff --check` passes.

### Reproduce

From the repository root, with Python 3 and a .NET 10 SDK available:

```sh
python3 tools/check_fft_lifecycle.py --dotnet /path/to/dotnet
python3 tools/check_lod_simulation.py --dotnet /path/to/dotnet
python3 tools/check_lod_resources.py --dotnet /path/to/dotnet
python3 tools/check_input_lifecycle.py --dotnet /path/to/dotnet
python3 tools/check_underwater_targets.py --dotnet /path/to/dotnet
/path/to/dotnet run --project tools/StaticChecks -- crest/Assets
git diff --check
```

Add `--ref 7a710032` to any of the five behavioral commands to reproduce its
baseline counterexamples. These projects clear NuGet sources and require no
downloaded test packages. They compile production source read from the selected
revision while keeping the checkout intact.

## Remaining work and limits

The changes above are bounded production fixes. They do not establish full Crest
correctness, a new device frame budget or a measured improvement in rendered
appearance. RHalf foam lifetime drift and the Android frame-budget findings remain
open, as documented in the earlier device reviews.

The following separate static candidates remain for a future focused pass:

- Runtime `OceanRenderer.Gravity` changes are not part of FFT spectrum cache
  invalidation, despite being read during spectrum initialization.
- Runtime Flow/dynamic-wave manager creation does not mirror initial `OnEnable`
  keyword setup after an off/on cycle in the graphics ping-pong combine path.
- Other input components, including sea-floor depth and the separate generic
  spline-input support class, need an ownership audit for their generated
  materials/meshes.
- Generic duplicate-key comparison and saturated-priority ties need a separate
  ordering contract if stable ties are required.

No additional rendering features or scene tuning are bundled into these fixes.
