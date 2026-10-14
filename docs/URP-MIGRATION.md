# Unity 6 / URP 17 migration

This is an independent port of the MIT-licensed built-in-renderer source in
`nx111/crest`, based on `db0658ff0b2e93e4a9e28cc2867509658b0ecc00`.
It does not include the commercial Crest URP package.

## Target and validation status

- Project target: **Unity 6000.6.2f1 / URP 17.6.0**.
- **Rendering changes after `2aaeec77`** add specular filtering, stable foam integration/sampling,
  corrected water-path attenuation and caustics, followed by planar reflection filtering,
  explicit normal gradients, precise shallow-water caches and corrected underwater Fresnel blending.
  Later batches correct geometric wave normals, FFT loop spectrum/timing, Gerstner phase
  precision, FFT baking and foam transport/input/compositing. The fourth batch reorients
  detail normals, gives local lights each foam Flow phase's normal/variance, and fixes
  baked FFT query coordinates, temporal interpolation and index boundaries.
  User-supplied tests of **`90a93293`** now cover Editor D3D11 (322 assertions),
  Windows D3D11/D3D12 and Android Vulkan Players (60 assertions each), including
  actual RNM/foam HLSL and baked FFT/Burst query contracts. The supplied list of
  213 production source hashes matches that commit. These are bounded test cases,
  not full visual or performance acceptance.
  See [URP-REALISM-IMPROVEMENTS.md](URP-REALISM-IMPROVEMENTS.md), the
  [reflection and shallow-water report](URP-REFLECTIONS-AND-SHALLOW-WATER.md), the
  [wave and foam stability report](URP-WAVES-AND-FOAM-STABILITY.md), and the latest
  [surface detail and wave query report](URP-SURFACE-DETAIL-AND-WAVE-QUERIES.md) for results, controls and limits.
- **Runtime follow-up:** bounded asynchronous query submission, exact registrar
  ownership, persistent coordinate compaction, safe output spans and callback-aware cleanup;
  test-runner startup errors now survive domain reload and Search errors remain fatal.
  The [runtime follow-up report](URP-RUNTIME-FOLLOWUP.md) also records the first Windows
  query-ring failure, RHalf foam lifetime drift, the original Android startup class error and
  the Android frame budget still unmet by the default examples.
- **Latest production retest (`c685e51e`):** query shutdown now waits only for the
  instance's own pending requests, the example supplies its missing Android startup
  dependency, and camera restoration follows URP's native/GPU projection conventions.
  The [retest report](URP-UNITY-RUNTIME-RETEST-9df1db9f.md) records 376 Editor assertions,
  129 on each Windows API and 182 on Android Vulkan, with actual main-screen captures
  and native shutdown checks. The final 242-file production manifest matches this
  commit. The [independent review](URP-RUNTIME-REVIEW-c685e51e.md) preserves those results
  and fixes diagnostic false-pass paths; the new diagnostic changes have offline
  checks, not a new Unity/device run.
- Renderer: **Universal Renderer with Render Graph enabled**, initially Forward,
  Linear color, MSAA off, render scale 1.0. The 2D Renderer is not supported.
- **Subsequent production static review (baseline `487442b3`):** fixes query layout
  ownership, baked-query coordinate compaction/output bounds, helper velocity
  availability, FFT resource-cache invalidation, zero-time dynamic-wave damping,
  negative-time Flow phases, horizon height precision and underwater camera/resource
  lifecycles. See the [source review](URP-PRODUCTION-STATIC-REVIEW.md) for counterexamples
  and offline results, including 250 selected DXC stages/variants. These production
  changes have no new Unity/device validation and do not inherit the earlier retest's
  visual or performance results.
- **Latest simulation/resource static review (baseline `7a710032`):** fixes FFT
  condition identity, safe shared-output reuse and final ownership, rounded
  fixed-step pause history, same-settings texture loss/recovery, first-frame
  cascade history, wave-family and spline resource ownership, input queue overflow,
  duplicate floating-origin compensation and Portal/Volume depth-target changes.
  Rebuilds preserve runtime time providers and release the old simulation command
  buffer. See the [lifecycle review](URP-SIMULATION-LIFECYCLE-REVIEW.md) for old-version
  counterexamples, allocation/recovery limits and reproduction commands. This
  continuation changes C# production code and has offline verification only.
- **Headless Unity import, C# API binding, pipeline creation, example asset conversion
  and configuration validation passed locally on Unity 6000.6.2f1 / URP 17.6.0.**
  This result predates the [document review follow-up](URP-DOCUMENT-REVIEW-FOLLOWUP.md),
  whose Unity compilation is deferred until the other build finishes.
  The project includes the generated pipeline assets and converted example materials;
  see [static-check-results.md](static-check-results.md).
- **The earlier `2aaeec77` D3D11 quality run passed on 2026-10-11 with 37 captured frames**: all seven
  example scenes, both fullscreen underwater paths, point/spot illumination and
  shadows, local highlights, probe box projection, selected transparent refraction,
  MSAA 1/2/4/8, render scale 0.75 and an orthographic smoke check.
  A basic Forward+ local-light check also passed.
- **Controlled original Built-in/URP surface shading matched with an image delta
  of 0**, using identical geometry, time, camera, material keywords, a single sun
  and fixed cubemap. Foam, scene refraction, shadows and caustics were disabled in
  this comparison. See [URP-QUALITY-AUDIT.md](URP-QUALITY-AUDIT.md) for evidence and limits.
- Full shader variant coverage and device/visual/performance validation beyond the
  reported cases remain pending. The final Android startup/exit log has no recorded
  errors, but BoatWakes and Lakes still miss the 30 FPS budget even at render scale 0.5.
  RHalf foam lifetime drift, Release/thermal measurements, XR and arbitrary camera
  stacks are not resolved by the current retest.
- This branch is ready for local compilation and visual review, not a declaration
  of tested visual equivalence or production readiness.

URP 17.6's official source removes the old compatibility execution path. This
port implements `RecordRenderGraph` with unsafe water passes and raster transparent
composition/color-copy passes, using declared camera
color/depth/shadow dependencies, imported persistent water-mask textures, and
explicit global state ordering. **Do not disable Render Graph.** Earlier Unity 6
releases require their corresponding URP 17 minor version; changing those pins is
a separate local compatibility test, not a tested configuration of this branch.

## Implemented changes

| Area | Change |
| --- | --- |
| Ocean shading | URP Core/Lighting, `UniversalForwardOnly`, opaque-color or selected-transparent refraction instead of GrabPass, URP main light/probe reflection and world-space caustic shadows. Additional lights contribute scattering, independent local highlights and foam lighting, with URP range/cone/shadow/cookie/layer attenuation. Existing shader names, properties and asset GUIDs are retained. |
| Render scheduling | `CrestUniversalRendererFeature`: shadows and water masks before scene depth/opaques, optional underwater fog before transparents, or underwater composition before post-processing. Camera matrices and viewport are restored after explicit simulation draws and custom camera commands. |
| Water masks | Persistent textures imported into Render Graph; descriptors follow the URP camera size; MSAA disabled on sampled mask/volume textures. |
| Underwater color | A graph-owned resolved color copy replaces an early-released temporary texture on the URP path. Stencil depth follows camera MSAA. Early fog refreshes opaque color for subsequent refraction. |
| Transparent refraction | Opt-in `Refracted Layers` draw ordinary URP transparents before water; a resolved full-resolution, bilinear-filtered color copy includes them in refraction. The layers are excluded from the renderer's normal transparent draw. Early objects skip per-object Crest fog, which is applied by fullscreen composition. |
| Ocean shadows | Main-light atlas sampling with distance fade for world queries, retaining Crest's persistent two-channel temporal accumulation without adding URP PCF to its hard channel. No URP dependency on `Light.AddCommandBuffer`. |
| Reflection probes | World-position blending/box projection, the relevant shader variants and example asset settings; clustered probe atlas variant included. |
| Camera requests | Planar reflection and depth-cache capture use `UniversalRenderPipeline.SingleCameraRequest`. Reflection cameras skip Crest's view-camera passes; water activation and culling state are restored in `finally`. |
| Tile bindings | Water tiles bind their per-camera data explicitly; no dependency on `Camera.current` to update reflection textures. |
| Examples | Surface Shaders and the gradient sky replaced with explicit URP passes; Standard/terrain materials and built-in default material references converted. The shared camera prefab includes URP camera data. Supported custom camera events select offscreen preparation, after-prepass callbacks or scene depth changes after opaques; see the document review follow-up for the mapping. |
| Project setup | URP package/assembly references, missing Splines dependency, stable metadata for new files, repeatable setup/validation menus. |

The FFT/Gerstner generation and wave-query/buoyancy architecture is retained. Later
changes correct wave phase precision, FFT loop spectrum/timing, geometric normals and
FFT readback and baked query coordinates/interpolation; foam uses exact source/decay
integration, midpoint flow backtracing, time-normalized built-in additive inputs and
filtered coverage/color sampling.
Surface optics and shading changes are detailed in the reports above. The
offscreen simulation input shaders that use UnityCG remain explicit draw shaders;
they do not require the built-in camera lighting or GrabPass pipeline.

## Local setup

1. Fetch and switch to `urp`. Open the **`crest` subdirectory** as the Unity project
   using Unity 6000.6.2f1. Let package resolution/import finish and save the editor log.
2. The example project already assigns `Assets/CrestURPSettings/CrestPipeline.asset`
   to Graphics and all quality levels. It enables Depth Texture and requests color
   copies per camera through Crest's feature, disables MSAA, and uses four shadow cascades over 150 units.
   Probe blending/box projection, additional-light shadows and soft shadows are enabled;
   the per-object additional-light limit is eight. These quality options add GPU cost.
   **Crest > URP > Create and Assign Pipeline** can recreate the generated assets.
   In an existing game, use its URP asset and **Add Feature to Selected Renderer**
   instead of replacing its Graphics/Quality settings.
3. Run **Crest > URP > Validate Active Pipeline**. Add the active
   `CrestUniversalRendererFeature` once to each relevant Universal Renderer.
   The build preprocessor also checks the current pipeline configuration.
4. The bundled Standard and terrain materials have been converted with Unity's
   material upgraders; built-in default materials in example prefabs reference URP
   defaults. **Crest > URP > Convert Example Assets** repeats this setup if upstream
   assets are restored. `Crest/Ocean` materials retain their shader GUIDs and properties.
5. Use `UnderwaterRenderer` on the view camera. Prefer it over the deprecated
   underwater curtain helper. Use the same directional sun for URP and
   `OceanRenderer._primaryLight`. Water/world-space shadow queries use URP's main-light
   atlas; complete the shadow-combination review below when enabling a screen-space
   shadow renderer feature.
6. To refract underwater transparent objects, assign them to **Crest Refracted**
   (layer 9 in the example project). Keep water on its existing layer. In another
   project, select its Universal Renderer Data and run **Configure Transparent
   Refraction**; this configures the feature and excludes the layer from the
   renderer's **Transparent Layer Mask**. Run validation afterwards. Regular URP
   transparent materials are supported. Clearing `Refracted Layers` also requires
   restoring those layers to the normal Transparent Layer Mask.
7. The example UI uses screen-space overlay canvases. Crest water fog/mask/copy
   processing runs on base Game/Scene View cameras and is excluded from overlay,
   preview and reflection cameras. The replacement selected-transparent draw also
   runs without an ocean and on reflection/overlay cameras so excluded objects remain visible.
8. Re-bake existing FFT collision assets after updating. The corrected loop spectrum
   and Time Scale handling must match both the displayed waves and baked CPU queries.
   Built-in additive foam inputs retain their default 30 Hz deposit; review wake
   strengths previously tuned at other simulation frequencies.
9. Review detail-normal strength on steep waves and local lights on 3D foam. Baked FFT
   provider displacement queries now invert the field at the query time; their spatial
   field sampling calls increase from two to eight. Common `SampleHeightHelper` calls
   also use this path. Profile the actual query workload on the target CPU.

## 本地复核清单

2026-10-11 的前一轮 `2aaeec77` 基线已在 Unity 6000.6.2f1 / URP 17.6.0 中重新编译和渲染。
该轮静态／数值回归与 37 帧 GPU 检查均通过，包含透明折射、局部光源、盒投影及
两种全屏水下路径。详细范围、对照图和仍待验收的差距见
[渲染质量复核报告](URP-QUALITY-AUDIT.md)；自动检查的复现步骤见
[UnityQualityTests](../tools/UnityQualityTests/README.md)。之后四批渲染质量修改
尚未重跑 GPU 检查，优先复核[高光、泡沫与光学项目](URP-REALISM-IMPROVEMENTS.md#5-本地画面验收与后续优先级)
、[反射、法线与浅水项目](URP-REFLECTIONS-AND-SHALLOW-WATER.md#6-本地画面与性能验收)
、[波浪时序、浪面法线与泡沫项目](URP-WAVES-AND-FOAM-STABILITY.md#6-本地画面与性能验收)
及[细节法线、泡沫局部灯与烘焙查询项目](URP-SURFACE-DETAIL-AND-WAVE-QUERIES.md#6-本地画面与性能验收)。
以下仍是完整人工验收项：

- [ ] **焦散位置**：浅水池底放置可辨识物体，分别从水上和水下观察；转动、平移相机，
  检查焦散贴合池底及遮挡物；远离原点后重复。对比新水下效果与旧 Curtain 路径。
- [ ] **实际 MSAA**：URP Asset 1x/2x/4x/8x、相机 Allow MSAA 开/关、目标 RT 的采样数
  分别组合；确认 Frame Debugger 中 `_CrestDepthTextureOffset` 在实际 1x 时为 0，多采样时为 1，
  Game/Scene View 及不同采样数相机交替渲染时无残留，水线无亮边或漏雾。
- [ ] **阴影坐标／变体**：主光无阴影、硬阴影、软阴影 Low/Medium/High；1/2/3/4 级联；
  Screen Space Shadows 开/关。检查海面累积、水上／水下焦散、Masked 不透明示例及透明水下示例。
  世界空间查询须读取图集，越出级联范围无错误遮挡；检查 Development Player 的变体裁剪结果。
- [ ] **探针混合**：两个颜色明显不同的反射探针设置重叠与 Blend Distance，关闭平面反射和自定义
  Cubemap 后观察交界；混合开/关均有合理结果，移动水面位置时过渡连续，再检查原有反射覆盖选项。

以下为完整回归清单；自动渲染检查的覆盖范围见上文，空复选框表示尚未完成该项的完整人工复核。

- [ ] **导入/编译**：清空 Console 后重新导入；无 C# 错误、Shader error、粉色材质、缺失脚本；保存 Editor.log。
- [ ] **基础场景**：打开 Ocean 示例；从第三人称、贴近水面和高空观察，FFT/Gerstner 波形、LOD 过渡、水平线没有接缝或跳变。
- [ ] **浮力与交互**：船体随波运动；波浪高度查询与画面一致；船尾迹、动态波、泡沫输入和 Flow 正常。
- [ ] **折射/水深**：浅滩、岸边、船体接触位置正确；无上下翻转、黑屏或画面固定的折射；分别检查 HDR 开/关、Render Scale 1.0/0.75。
- [ ] **水下**：相机缓慢穿越水面；遮罩、弯月线、雾色、焦散连续；切换场景及主相机后没有残留。
- [ ] **透明物体**：测试 `Enable Shader API` 开/关；透明物体使用示例 URP 水下着色器时，雾只应用一次；早期水下雾后的海面折射正确。
- [ ] **体积水**：Portal、Volume、Volume Fly-Through；相机进入/离开；模板测试不影响其他效果；MSAA 1x/2x/4x 分别检查纹理样本数错误。
- [ ] **阴影**：主光开/关、硬/软阴影、级联距离；海面和水下焦散正确受遮挡，移动相机无阴影拖尾；关闭太阳后不残留旧阴影。
- [ ] **平面反射**：开启/关闭、移动相机、调整刷新频率；无递归渲染、海面丢失、倒置或 RT 泄漏；反射请求异常后主相机状态恢复。
- [ ] **深度缓存**：分别运行实时填充和烘焙读取；核对地形高度、相对高度、浅水泡沫和裁切；重新填充后数据更新。
- [ ] **多相机/生命周期**：Game/Scene View 来回切换；禁用/启用 OceanRenderer、UnderwaterRenderer 和 Renderer Feature；重载域/关闭域重载两种情况；纹理、全局关键字不串相机。
- [ ] **Frame Debugger / Render Graph Viewer**：可见 Crest mask/shadow/underwater passes；依赖和先后顺序正确，无已释放资源、backbuffer 读取或 native render-pass 错误。
- [ ] **Windows 构建**：Development Build，D3D11 首测，再测 D3D12；重启独立播放器检查 Shader variant stripping，尤其水下和阴影组合。
- [ ] **Android 真机**：ARM64、Vulkan、IL2CPP；首次加载、暂停恢复、场景切换各测一次；记录 GPU/CPU 帧时、峰值内存、RT 内存及首次 FFT/深度缓存时延。沿用上游限制：不支持 OpenGL ES / WebGL。

Forward+ 已通过局部光源基础检查；完整 Forward+、Deferred、XR、多视图、动态分辨率、
第三方屏幕空间效果与任意自定义相机堆栈仍需要另行复核。

## Reproduce static checks

From the repository root:

```text
python tools/check_urp.py
python tools/check_urp_regressions.py -v
python tools/check_wave_normals.py
python tools/check_wave_spectrum.py
python tools/check_foam_dynamics.py
python tools/check_foam_shading.py
dotnet run --project tools/StaticChecks -- crest/Assets
git diff --check
```

The syntax checker uses the .NET 10 SDK's bundled Roslyn, with no NuGet package
dependencies. It parses C# 9 under Unity 6.0/6.6 Editor/Player symbol sets. This
does **not** resolve Unity assemblies or verify shader code generation.

The source checker checks shader include paths, preprocessor/program-block
balance, unsupported Surface Shader/GrabPass constructs, assembly JSON, package
pinning, and metadata for new Unity assets.

The targeted regression checks read the actual reconstruction expressions at all
three caustic call sites, including the current shared helpers, and compare 1458
points against camera-local ground truth. Cases include perspective/orthographic
projection, depth conventions, refracted UVs, rotated cameras and large world offsets. They also check
per-camera MSAA recording, all main/soft-shadow keyword groups, atlas cascade
coordinates (including screen-shadow variants and the no-shadow sentinel), surface
shadow routes and the reflection-probe blending API. These do not compile HLSL or
replace the local visual checks above. To demonstrate the previous coordinate bug:

```text
python tools/check_urp_regressions.py --ref 175fe207 URPRegressions.test_reconstructed_points_are_in_front_with_correct_eye_depth
```

That command is expected to fail on the old source.

The wave/foam regression suite, selected DXC shader matrix and the four new
checks against baseline `8b56660e` are documented in
[wave and foam verification](URP-WAVES-AND-FOAM-STABILITY.md#5-最终验证与复现).
Later checks are documented in the [surface detail report](URP-SURFACE-DETAIL-AND-WAVE-QUERIES.md#5-最终验证与复现)
and [runtime follow-up](URP-RUNTIME-FOLLOWUP.md#8-本轮验证与复测要求).
The subsequent [Unity/device retest](URP-UNITY-RUNTIME-RETEST-9df1db9f.md) and
[independent review](URP-RUNTIME-REVIEW-c685e51e.md) record the current production
evidence and stricter host/diagnostic acceptance rules.

See [static-check-results.md](static-check-results.md) for the results recorded
for this migration. Reproduce actual GPU checks using
[tools/UnityQualityTests](../tools/UnityQualityTests/README.md) in a disposable project copy.

## API references

- [Unity URP Render Graph unsafe pass documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/render-graph-unsafe-pass.html)
- [Official UnsafePass sample](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Samples~/URPRenderGraphSamples/UnsafePass/UnsafePassRenderFeature.cs)
- [URP RenderGraphSettings (removed compatibility switch)](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Runtime/Settings/RenderGraphSettings.cs)
- [URP render request implementation](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Runtime/UniversalRenderPipeline.cs)
