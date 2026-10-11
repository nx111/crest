# URP 渲染质量复核与改进

日期：2026-10-11。工作分支：`urp`，本轮修改前提交：`3dd1c82b`。
原始 Built-in 基线：`db0658ff0b2e93e4a9e28cc2867509658b0ecc00`。
参考材料：用户提供的《审核 URP 改造的渲染质量.md》；本报告重新核对了其中的判断。

本轮已补充局部灯光、透明折射、反射探针和阴影细节，并在
**Unity 6000.6.2f1 / URP 17.6.0 / Windows D3D11 / GTX 1650** 上完成实际渲染验证。
在固定几何、波浪时间、相机、单太阳和 Cubemap 的海面着色对照中，原始 Built-in
Shader 与当前 URP 的线性 RGB 图像差值为 **0**。这个结论限于该受控海面案例；
完整场景、透明排序、水下体积边缘及不同设备仍有独立验收项。

## 参考文档的复核

| 问题 | 原始源码与当前实现 | 本轮结论 |
| --- | --- | --- |
| GrabPass 与透明折射 | 原版海面队列为 `Geometry+510`（2510），GrabPass 只抓取海面绘制前已有的颜色。默认队列 3000 的透明物体通常在海面之后绘制。URP opaque color 确实丢失已提前绘制的透明颜色。 | 存在折射范围差异，但不能认定原版自动折射所有普通透明物体。本轮增加可选图层的海面前透明合成，普通 URP 透明材质也能参与折射。 |
| Mask 强制单采样 | 原版 `UnderwaterRenderer.Mask.cs` 已设置 `msaaSamples = 1`；URP 增加 `bindMS = false`。 | 单采样遮罩是沿用原版设计。水线仍需边缘质量复核，不能仅据此认定移植降低了原本多采样的 Mask。 |
| 点光源／聚光灯 | 原版海面只有 `ForwardBase`，没有 `ForwardAdd` 或附加逐像素灯光循环；原版保留环境照明。修改前 URP 海面也没有局部光源循环。 | 局部照明是有价值的能力缺口，本轮作为增强补齐；不把它表述为丢失原版已有的完整逐像素多光源支持。 |
| Screen Space Shadows | URP 自己在透明物体阶段关闭屏幕空间阴影、恢复主光图集／级联关键字。屏幕空间纹理使用不透明深度，不适合任意水面与水下世界坐标。 | 图集采样适合这些查询。真正需修正的是阴影淡出和 Crest 累积通道的滤波语义；本轮已修正。 |
| 探针盒投影 | 修改前已向 `GlossyEnvironmentReflection` 传入世界位置，但海面缺少盒投影变体，示例 Pipeline 也关闭盒投影／混合。原版反射助手未显式调用盒投影方向变换。 | 之前的世界位置修复不足以启用盒投影。本轮补齐变体和设置，按反射能力增强记录。 |
| Forward+／Deferred | 修改前文件没有完整的这些组合验收记录。 | 验证不足不能直接等同确定失效。本轮通过 Forward+ 的局部光源基础检查；完整 Forward+／Deferred 仍需另行验收。 |

原版依据：
[Ocean.shader](https://github.com/nx111/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/Ocean.shader)、
[UnderwaterRenderer.Mask.cs](https://github.com/nx111/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Scripts/Underwater/UnderwaterRenderer.Mask.cs)、
[OceanReflection.hlsl](https://github.com/nx111/crest/blob/db0658ff0b2e93e4a9e28cc2867509658b0ecc00/crest/Assets/Crest/Crest/Shaders/OceanReflection.hlsl)。
URP 透明阴影顺序依据其
[ScreenSpaceShadows.cs](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.universal/Runtime/RendererFeatures/ScreenSpaceShadows.cs)。

## 本轮实现

### 局部光源照亮海面

新增 [OceanAdditionalLights.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanAdditionalLights.hlsl)，
使用 URP 光源数据与灯光循环，计算点光／聚光灯的水体散射、局部反射高光和泡沫照明。
采用 URP 的距离、光锥、阴影和 Cookie 衰减，并匹配 Rendering Layers。
支持逐像素设置、逐顶点 URP Asset 设置及 `_CLUSTER_LIGHT_LOOP`；逐顶点设置下，
海面仍逐像素计算局部光源，避免大尺寸海面网格漏掉局部照明。
两个自定义示例着色器也补充相应变体及 `vertexLighting`。

海面材质新增 `Point and Spot Light Intensity`（默认 1）、
`Point and Spot Highlight Boost`（默认 7）和 `Point and Spot Highlight Falloff`
（默认 275）。局部高光独立于太阳高光开关；附加光强度设为 0 可保留原有外观。
这些贡献沿用 Crest 的材质形状与吸收合成，不代表新增完整物理水体多次散射模型。

### 普通透明物体参与折射

新增 [CrestRefractionPass.cs](../crest/Assets/Crest/Crest/Scripts/URP/CrestRefractionPass.cs)：
在海面前绘制选中图层的透明物体，再按相机当前渲染分辨率保存单采样颜色到 Render Graph
管理的纹理。海面和旧 Curtain 路径通过 `CrestSampleBackground` 选择这张纹理；
未启用选中图层时继续使用 URP opaque color。颜色副本使用双线性过滤和 Clamp，
避免扰动 UV 采用默认 Point 过滤产生跳动边缘。

顺序为：不透明场景 → 选中透明物体（448）→ 可选早期水下雾（449）→
折射颜色副本（450）→ 海面及其他透明物体 → 可选晚期水下合成（550）。
提前绘制的透明物体恢复 URP 主光图集／级联关键字；Crest Shader API 在此绘制时跳过
单物体雾，由随后的全屏路径处理，避免重复施雾。
深度缓存等仅深度目标不执行颜色折射；图层的替代绘制也保留在无海面、反射和 Overlay
相机路径中，避免从普通透明 Mask 排除后消失。这些相机组合尚未逐一做 GPU 验收。

使用方法：

1. 示例 Renderer 已配置 **Crest Refracted**（第 9 层）。把需要折射的水下透明 Renderer
   放到该层；海面保持现有层。
2. 在其他项目中选中实际使用的 Universal Renderer Data，执行
   **Crest > URP > Configure Transparent Refraction**。菜单创建／复用图层，设置
   Feature 的 `Refracted Layers`，并从 Renderer 的 `Transparent Layer Mask` 排除它。
3. 执行 **Validate Active Pipeline**，检查重复 Feature、双重绘制和已加载海面的图层冲突。
4. 关闭该功能时，清空 Feature 的 `Refracted Layers`，并把原选中图层加入 Renderer 的
   `Transparent Layer Mask`，恢复普通透明绘制。

绘制支持 `SRPDefaultUnlit`、`UniversalForward` 和 `UniversalForwardOnly` 的透明 Pass。
已用默认队列 3000 的普通 URP Unlit 材质验证，不要求改写为 Crest 专用 Shader。
适合水下物体及特效；提前合成仍会改变与其余透明物体的排序关系。透明物体没有独立
逐像素深度时，折射厚度和全屏水下雾仍依据不透明深度，不解决所有交叠、跨水面或排序问题。
需要按自身世界坐标精确施雾的普通透明物体，可继续在正常绘制图层使用原 Shader API。

### 探针、阴影与默认质量

- 海面与示例 Shader 补齐盒投影／探针 Atlas 变体；示例 Pipeline 和创建菜单启用探针混合、
  盒投影、附加光阴影和软阴影，逐对象附加灯光上限从 4 提高到 8。
- 主光颜色纳入 URP 实际主光可见性；世界空间焦散阴影补上主光距离淡出。
- Crest 时间累积阴影使用原始阴影比较采样，保留硬／软两个通道由世界空间抖动产生的差别，
  避免硬通道被额外套用 URP PCF。累积 Shader 已有淡出，调用时关闭重复淡出。
- 每个水相机在准备阶段重置折射全局状态；复制纹理由 Render Graph 管理。

默认增强会增加照明、阴影和反射成本。选中折射图层还会增加透明绘制与一次全分辨率
颜色副本；弱 GPU 应按场景需求减少灯光、阴影或关闭选中图层。当前没有帧时或显存
预算测量，不能据此承诺原版性能相同。

## 实际验证结果

测试在独立临时项目副本中运行，不给源项目生成 Library。完整记录：
[results.txt](_media/urp-quality/2026-10-11/results.txt)；
复现步骤：[UnityQualityTests](../tools/UnityQualityTests/README.md)。

| 验证项 | 结果与边界 |
| --- | --- |
| Unity API 绑定／编译 | 当前代码实际导入、编译、进入 Play Mode；最终 `CREST_QUALITY_RESULT PASS`，Editor 正常退出。 |
| 点光／聚光灯 | 隔离海面后实际照亮；逐像素 RGB 绝对差之和的平均值分别约 0.006127、0.001236。零强度、超距离及背向光锥返回暗场。 |
| 局部高光／阴影 | 关闭太阳高光仍产生局部高光；开启聚光灯投影后遮挡海面变暗。 |
| 逐顶点／Forward+ | 逐顶点 Asset 下局部照明有效；Forward+ 集群局部光循环有效。只覆盖这些基础案例。 |
| 反射探针 | 非对称 Cubemap 与本地盒投影开／关产生可测图像差异。重叠探针的完整移动过渡尚未人工验收。 |
| 透明折射 | 队列 3000 的普通 URP 透明物体出现在海面折射中；法线扰动开／关产生可测差异。 |
| 水下／相机 | 早期和晚期全屏合成都有可见雾色，无 Shader／Render Graph 错误；MSAA 1/2/4/8 的水线偏移为 0/1/1/1；Render Scale 0.75 与正交相机完成渲染。不是所有水线／焦散边缘的质量结论。 |
| 七个示例 | `boat`、`BoatWakes`、`threeboats`、`Examples`、`LakesAndRivers`、`main`、`whirlpool` 均完成材质 Pass 编译与实际渲染；无错误材质像素。 |
| 原始 Built-in 对照 | 从基线提取原始 Ocean Shader 和 31 个 include，只改 Shader 名；相同模拟几何上切换实际管线和 Shader。960 × 540 线性浮点 RGB 图像差值为 **0**。 |
| 静态／数值 | 100 个 Shader/HLSL/Compute，185 个 include，6 个 asmdef，14 个新增 Unity 资源元数据检查：0 错误；146 个 C# × 4 条件配置：0 错误；既有 9 项回归全部通过，含 243 个重构点。 |

本轮产生 37 张验证截图，保存了 13 张关键对照图。浮点差值指标为
`mean(abs(deltaR) + abs(deltaG) + abs(deltaB))`，PNG 为转换到 Gamma 的预览图。
原版对照关闭泡沫、场景折射、阴影、焦散、Albedo 和平面反射，使用相同材质关键字、
冻结时间与固定 Cubemap；它验证核心海面散射／反射公式，不能外推整个原始项目完全等价。
最终日志另有一条 Unity Editor Search 索引异常，堆栈在 `SearchDatabase`；测试单独记录，
没有把它计为渲染错误。运行时、Shader 和 Render Graph 错误仍会使测试失败。

### 关键图像

| 对照 | 图像 |
| --- | --- |
| 局部灯光关闭／点光照亮 | [关闭](_media/urp-quality/2026-10-11/02-lights-off.png) · [点光](_media/urp-quality/2026-10-11/03-point-light.png) · [局部高光](_media/urp-quality/2026-10-11/04c-additional-highlight-on.png) |
| 探针盒投影关闭／开启 | [关闭](_media/urp-quality/2026-10-11/11b-probe-no-box-projection.png) · [开启](_media/urp-quality/2026-10-11/11c-probe-box-projection.png) |
| 透明折射关闭／开启／法线扰动 | [仅不透明颜色](_media/urp-quality/2026-10-11/12-opaque-only-refraction.png) · [透明无扰动](_media/urp-quality/2026-10-11/13a-transparent-no-distortion.png) · [透明折射](_media/urp-quality/2026-10-11/13-transparent-refraction.png) |
| 水下两种合成 | [晚期](_media/urp-quality/2026-10-11/14-underwater-late.png) · [早期](_media/urp-quality/2026-10-11/15-underwater-early.png) |
| 原版／当前海面着色对照 | [原始 Built-in](_media/urp-quality/2026-10-11/reference-builtin-surface.png) · [URP](_media/urp-quality/2026-10-11/reference-urp-surface.png) |

![当前 main 场景](_media/urp-quality/2026-10-11/scene-main.png)

## 仍需验收的画质差距

- 透明物体交叠、横跨水面及不同队列的排序；透明物体自身深度对应的雾和吸收。
- 单采样 Mask 的细水线、弯月线、运动走样，以及 Portal／Volume／Fly-Through 模板和
  MSAA 组合；本轮没有宣称这些边缘已与原版逐像素等价。
- 真实浅水场景中焦散随相机／大世界坐标变化的人工对照，正交焦散，以及原版完整
  阴影、泡沫、平面反射和探针过渡的匹配。本轮 GPU 冒烟与数值回归不替代这些对照。
- 全部主光硬／软阴影、级联与 Screen Space Shadows 组合，完整 Forward+／Deferred、
  多相机堆栈、动态分辨率、XR、第三方屏幕空间效果。
- Development Player 的 Shader stripping 与启动结果，D3D12、Android／Vulkan 真机，
  性能／显存预算；这些均未运行。

详细验收清单保留在 [URP-MIGRATION.md](URP-MIGRATION.md)。
