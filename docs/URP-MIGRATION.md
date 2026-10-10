# Unity 6 / URP 17 migration

This is an independent port of the MIT-licensed built-in-renderer source in
`nx111/crest`, based on `db0658ff0b2e93e4a9e28cc2867509658b0ecc00`.
It does not include the commercial Crest URP package.

## Target and validation status

- Project target: **Unity 6000.6.0f1 / URP 17.6.0**.
- Renderer: **Universal Renderer with Render Graph enabled**, initially Forward,
  Linear color, MSAA off, render scale 1.0. The 2D Renderer is not supported.
- **Headless Unity import, C# API binding, pipeline creation and validation passed locally
  on Unity 6000.6.2f1 / URP 17.6.0.** The editor automatically migrated older package
  dependencies in the validation copy; see [static-check-results.md](static-check-results.md).
- Shader variant compilation, player builds and GPU/device validation have not been
  run. The exact Unity 6000.6.0f1 editor target has not been tested.
- This branch is ready for local compilation and visual review, not a declaration
  of tested visual equivalence or production readiness.

URP 17.6's official source removes the old compatibility execution path. This
port implements `RecordRenderGraph` with `AddUnsafePass`, using declared camera
color/depth/shadow dependencies, imported persistent water-mask textures, and
explicit global state ordering. **Do not disable Render Graph.** Earlier Unity 6
releases require their corresponding URP 17 minor version; changing those pins is
a separate local compatibility test, not a tested configuration of this branch.

## Implemented changes

| Area | Change |
| --- | --- |
| Ocean shading | URP Core/Lighting, `UniversalForwardOnly`, opaque-color refraction instead of GrabPass, URP main light/probe reflection and world-space caustic shadows. Material shader names, property names and existing asset GUIDs are retained. |
| Render scheduling | `CrestUniversalRendererFeature`: shadows and water masks before scene depth/opaques, optional underwater fog before transparents, or underwater composition before post-processing. |
| Water masks | Persistent textures imported into Render Graph; descriptors follow the URP camera size; MSAA disabled on sampled mask/volume textures. |
| Underwater color | A graph-owned resolved color copy replaces an early-released temporary texture on the URP path. Stencil depth follows camera MSAA. Early fog refreshes opaque color for subsequent refraction. |
| Ocean shadows | Main-light shadow atlas sampling, retaining Crest's persistent two-channel shadow accumulation. No URP dependency on `Light.AddCommandBuffer`. |
| Camera requests | Planar reflection and depth-cache capture use `UniversalRenderPipeline.SingleCameraRequest`. Reflection cameras skip Crest's view-camera passes; water activation and culling state are restored in `finally`. |
| Tile bindings | Water tiles bind their per-camera data explicitly; no dependency on `Camera.current` to update reflection textures. |
| Examples | Two Surface Shaders replaced with explicit URP passes; depth-only/mask shaders updated. Example custom camera commands now run after the water mask, before scene depth/opaques. Serialized BIRP `CameraEvent` values do not define URP execution order. |
| Project setup | URP package/assembly references, missing Splines dependency, stable metadata for new files, repeatable setup/validation menus. |

FFT/Gerstner generation, wave query and buoyancy algorithms are retained. The
offscreen simulation input shaders that use UnityCG remain explicit draw shaders;
they do not require the built-in camera lighting or GrabPass pipeline.

## Local setup

1. Fetch and switch to `urp`. Open the **`crest` subdirectory** as the Unity project
   using Unity 6000.6.0f1. Let package resolution/import finish and save the editor log.
2. Run **Crest > URP > Create and Assign Pipeline**. This creates assets in
   `Assets/CrestURPSettings`, enables Depth/Opaque Texture, and assigns the pipeline
   to Graphics and all quality levels. It is idempotent for the generated assets.
   In an existing game, use its URP asset and **Add Feature to Selected Renderer**
   instead of replacing its Graphics/Quality settings.
3. Run **Crest > URP > Validate Active Pipeline**. Add the active
   `CrestUniversalRendererFeature` once to each relevant Universal Renderer.
   The build preprocessor also checks the current pipeline configuration.
4. Convert the **upstream example Standard/terrain/particle materials** locally with
   Unity's Built-in-to-URP Render Pipeline Converter. Those serialized scene assets
   are retained; the source port does not pretend to have run Unity's converter.
   Inspect custom/third-party materials that the converter cannot convert.
   `Crest/Ocean` materials keep their shader GUID and do not need replacement.
5. Use `UnderwaterRenderer` on the view camera. Prefer it over the deprecated
   underwater curtain helper. Use the same directional sun for URP and
   `OceanRenderer._primaryLight`; initially disable screen-space shadow renderer
   features and use URP's main-light atlas shadows.
6. Keep overlay cameras for UI. Crest water processing runs on base Game/Scene View
   cameras and is deliberately excluded from overlay, preview and reflection cameras.

## 本地复核清单

以下项目全部需要本地主机执行；空复选框表示未验证。

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

Forward+、Deferred、XR、多视图、动态分辨率、第三方屏幕空间效果与任意自定义
相机堆栈都需要另行复核；本次没有为这些组合提供已通过的运行结果。Unity 的
旧后处理栈组件也不会自动成为 URP Volume，示例中的后处理视觉需本地转换。

## Reproduce static checks

From the repository root:

```text
python tools/check_urp.py
dotnet run --project tools/StaticChecks -- crest/Assets
git diff --check
```

The syntax checker uses the .NET 10 SDK's bundled Roslyn, with no NuGet package
dependencies. It parses C# 9 under Unity 6.0/6.6 Editor/Player symbol sets. This
does **not** resolve Unity assemblies or verify shader code generation.

The source checker checks shader include paths, preprocessor/program-block
balance, unsupported Surface Shader/GrabPass constructs, assembly JSON, package
pinning, and metadata for new Unity assets.

See [static-check-results.md](static-check-results.md) for the results recorded
for this migration.

## API references

- [Unity URP Render Graph unsafe pass documentation](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/render-graph-unsafe-pass.html)
- [Official UnsafePass sample](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Samples~/URPRenderGraphSamples/UnsafePass/UnsafePassRenderFeature.cs)
- [URP RenderGraphSettings (removed compatibility switch)](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Runtime/Settings/RenderGraphSettings.cs)
- [URP render request implementation](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Runtime/UniversalRenderPipeline.cs)
