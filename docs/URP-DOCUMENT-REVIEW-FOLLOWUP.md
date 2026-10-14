# URP document review follow-up

Date: 2026-10-14 (UTC+08). Source review baseline: `f405dc301` on `urp`.

This change checks the eight findings in the user-supplied Gemini review against
the current production code and the installed Unity 6000.6.2f1 / URP 17.6.0 sources.
The review's sample patches are proposals; several descriptions do not match this
baseline.
Unity compilation is deferred at the user's request until the other Unity build
finishes. The checks below are offline and do not establish GPU rendering quality
or Android performance.

## Findings and changes

| Review item | Current finding and action |
| --- | --- |
| 1. Shadow double flip and cleared graph commands | Not present in this baseline. `UpdateLodData` flips once; `BuildCommandBuffer` neither clears the caller's buffer nor flips. Preserve this ownership and add a source regression. |
| 2. Refracted transparent fog result | Confirmed. Classify the water mask and portal front depth before deferring water fog. Submerged refracted fragments return `true`, suppressing the caller's atmospheric `MixFog`; fragments outside the water still return `false`. |
| 3. Custom camera callbacks | Confirmed. Keep offscreen mask preparation before scene prepasses; add callbacks after prepasses and after opaques. Scene callbacks declare and bind the active camera color/depth attachments, restore the target and viewport afterwards, and subscribe according to the configured `CameraEvent`. Runtime event changes replace the old subscription. |
| 4. Overlay and volume resources | Partially confirmed. Overlay transitions were handled only in Editor; apply the existing enable/disable transition in Player too. Wrap volume targets only when configured and used. Publish neutral graph textures for unused faces, overlay masks and inactive underwater cameras, and clear released texture bindings on disable. |
| 5. Redundant color copies and renderer state | Confirmed, with an additional asset cause. Neither the replacement transparent draw nor the custom color copy requests URP's sampled Color/Depth inputs. Surface setup requests opaque color only without the custom refraction copy, or for early shader API fog. Disable the example pipeline's unconditional opaque-color request and update its creation/validation tools. Bind tile data at `beginCameraRendering`, before renderer-list recording. |
| 6. Reflection culling and quality overrides | Confirmed for the reflection's shadow draw. Mark only Crest's synchronous mirrored-camera request; invert scene culling after shadows, restore after transparents, and restore request ownership in `finally`. Reflection probes are excluded. Restrict legacy quality overrides to Built-in; URP continues to use per-camera `renderShadows`. Main-camera shadow-map corruption was not independently established. |
| 7. Gravity cache and graphics combine keywords | Confirmed. Include ocean gravity in FFT condition identity, snapshot/rebind it per generator, and reuse compatible idle resources without overwriting this frame's outputs. Synchronize graphics-combine Flow/Dynamic Waves keywords on runtime subsystem changes. |
| 8. Half-precision foam decay | Confirmed in the existing RTZ storage model. Choose an exact adjacent half value with probability proportional to distance. Seed by texel, LOD and actual simulation dispatch; preserve zero-time and exact-value paths, subnormal spacing, R32 formats and the existing post-integration coverage clamp. |

### Resource and copy details

The document's stale `RTHandle.referenceSize` explanation is not applicable to
these external wrappers. Core 17.6's `RTHandleSystem.Alloc(RenderTexture)` keeps
the same texture with automatic scaling disabled; `RenderGraphResourceRegistry`
reads its current descriptor when importing it. Releasing and recreating wrappers
on every resolution change is unnecessary.
The volume back target's Portal-to-Volume descriptor transition was already fixed
by the preceding lifecycle review.
Global graph texture publication follows Unity's
[SetGlobalTextureAfterPass contract](https://docs.unity3d.com/ja/current/Manual/urp/render-graph-create-global-texture.html).

The custom refraction color copy still runs whenever refracted layers are
configured, even when no selected objects are visible. Other renderer features or
an explicitly enabled camera/pipeline Opaque Texture option can still request
URP's opaque copy. Early shader API fog legitimately retains opaque color for its
consumers. No frame-time improvement is claimed without a new GPU profile.

Supported example callback mapping:

| CameraEvent | URP callback |
| --- | --- |
| BeforeDepthTexture, BeforeDepthNormalsTexture, BeforeForwardOpaque | After the water mask, before scene prepasses; offscreen preparation |
| AfterDepthTexture, AfterDepthNormalsTexture | After scene prepasses |
| AfterForwardOpaque, BeforeForwardAlpha | After scene opaques, with active camera attachments |

Other Built-in events emit an explicit unsupported-event message on subscription.
The mapping is limited to the example's supported stages, rather than treating
every Built-in event as the same URP event.

### Foam numerical evidence

The original suggested noise depends on a constant timestep, so it repeats for a
stationary texel at a fixed simulation frequency. Adding a fixed half ULP can also
freeze slow decay. The implemented per-dispatch seed changes on each real substep,
including multiple substeps in one frame.
Writing an exactly representable half endpoint makes the final store independent
of whether the hardware uses RTZ or RNE. This addresses the observed D3D11 model
without assuming a universal Vulkan rounding mode; see the
[D3D11 floating-point conversion rules](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm)
and [Vulkan format conversion rules](https://docs.vulkan.org/spec/latest/chapters/fundamentals.html#fundamentals-fp-conversion).

For an initial value of 1, fade rate 0.8/s and duration 2 seconds, the CPU model
using the production helpers gives these spatial means over 1,024 samples:

| Simulation rate | Corrected mean error |
| --- | ---: |
| 15 Hz | -0.00087% |
| 30 Hz | -0.00046% |
| 60 Hz | -0.00022% |
| 200 Hz | -0.03163% |

The uncorrected 200 Hz RTZ model gives approximately -12.69%.
The 200 Hz slow-decay case at 0.02/s gives -0.02315%; the 20-second case at
0.05/s gives -0.15646% over 256 samples.
These are deterministic CPU model measurements, not GPU readbacks.
Individual texels retain stochastic quantization variance, and shader execution
cost and visual stability require GPU checks.

## Verification

The executed checks passed: renderer behavior **43/43** and source/asset contracts
**4/4**; FFT **35/35** plus **12** graphics-combine state transitions; reflection
**5** source checks and **5** executable groups; foam rounding **9/9**, historical
storage **9/9**, foam dynamics **13/13**, and foam equations **8/8**; underwater
targets **6/6** and lifecycle **11/11** in both Player and Editor; LOD resources
**23/23**, LOD simulation **7/7**, reflection equations **14/14**, wave spectrum
**12/12**, and the existing URP regressions **9/9**.
C# syntax parsing passed for **148 production files** and **12 diagnostic files**,
each under four Unity define configurations. This is not Unity API binding.

The renderer harness against `f405dc301` fails **27 behavior cases** and **3 asset
contracts**; the shadow ownership contract passes on both versions.
The focused FFT test also reproduces missing gravity and combine-keyword updates
on that baseline.

The separate Android archived-evidence suite cannot complete: two fixture checks
produce three missing-file errors for `android-final-results.txt`,
`android-empty-no-dependency-logcat.txt` and `android-empty-with-dependency-logcat.txt`
under `docs/_media/urp-realism/2026-10-11-9df1db9f/`. Those files are absent from this
checkout and from the baseline's tracked tree. No replacement evidence or passing
device result is inferred.

Run from the repository root:

```text
python tools/check_urp.py
python tools/check_urp_regressions.py -v
python tools/check_urp_rendering_review.py
python tools/check_planar_reflection_culling_issue6.py
python tools/check_fft_gravity_shapecombine_review7.py
python tools/check_foam_issue8_stochastic_rounding.py
python tools/check_foam_storage.py
python tools/check_foam_dynamics.py
python tools/check_foam_quality.py
python tools/check_underwater_targets.py
python tools/check_underwater_lifecycle.py
python tools/check_lod_resources.py
python tools/check_lod_simulation.py
python tools/check_reflection_quality.py
python tools/check_wave_spectrum.py
dotnet run --project tools/StaticChecks -- crest/Assets
git diff --check
```

The focused FFT, renderer and reflection checks compile production C# methods
against managed API substitutes. The foam checks evaluate production helpers with
binary32 and half-storage models. Neither substitutes nor source contracts validate
Unity API binding, native resources, Render Graph execution or device behavior.
The actual foam shader has also passed offline D3D11 `cs_5_0` compilation with the
pinned Unity headers.

After the other build finishes, run Unity import/C# compilation and pipeline
validation. GPU follow-up should cover actual shadow data, refracted transparent
atmospheric fog, scene depth callbacks, Player overlay/mode transitions, mirrored
cameras with shadows, and spatially averaged foam lifetime on D3D11 and Vulkan.
Earlier device reports do not validate this patch.
