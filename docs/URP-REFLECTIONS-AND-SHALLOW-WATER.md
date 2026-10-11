# URP 真实度改进（二）：反射过滤、法线采样与浅水精度

日期：2026-10-11。分支：`urp`。本轮基线：
[`a61488b9`](https://github.com/nx111/crest/commit/a61488b97c3fd46729d9b0699d55f40d6a1bd843)。
目标版本保持 **Unity 6000.6.2f1 / URP 17.6.0**。

本轮接续[高光、泡沫与水体光学改进](URP-REALISM-IMPROVEMENTS.md)，已实施平面反射
粗糙度过滤、法线显式梯度采样、浅水高度精度修复，以及水下 Fresnel／全反射修正。
验证结果为 **173 / 173 个离线着色器编译用例、63 / 63 项数值／源码回归通过**，
146 个 C# 文件在 4 组 Unity 条件编译配置下语法检查无错误。

**本轮没有运行 Unity Editor、GPU 画面对照、Player 或 Android 真机检查。**
此前真实 D3D11 画面记录属于 `2aaeec77`，不能作为后续两批改动的视觉验收。
本文所述画面改善是由问题机制推导的预期效果，数值示例来自实际源码表达式的 CPU 检查。

## 研究结论与实施范围

| 定位的问题 | 已实施处理 | 预期改善 |
| --- | --- | --- |
| 平面反射固定读取 mip 0，粗糙水面仍保留尖锐倒影，屏幕边缘截断明显 | 按粗糙度与像素足迹选择 mip，边缘渐隐至环境反射，修正过滤后的覆盖合成 | 降低远处倒影闪烁、粗糙浪面过锐及反射边缘暗线 |
| 法线远级采样位于变化的 LOD 分支内；流场会改变真正的纹理足迹 | 在分支前计算包含流场位移的 UV 梯度，近远级使用对应梯度 | 让细浪过滤随观察角度、流场和 LOD 变化保持一致 |
| 正交缓存远平面端点有误；绝对高度过早转为 half；烘焙先经过 half 读回 | 修正正交重建，保持高度 float 链，RFloat 读回并写 float EXR | 减少岸线定位误差、高水位浅层水深丢失及水面／遮罩错位 |
| 水下透射和内部反射使用不同角度的 Fresnel 系数 | 使用统一系数与互补权重，显式处理全反射并限制数值域 | 修正临界角附近不合理的亮度损失 |

## 1. 平面反射：过滤、覆盖与资源更新

实现：[OceanPlanarReflection.cs](../crest/Assets/Crest/Crest/Scripts/Reflection/OceanPlanarReflection.cs)、
[OceanReflection.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanReflection.hlsl)。

### 过滤与材质控制

现有反射颜色 RT 默认启用 mip 链，使用 Trilinear、Clamp、单采样，并关闭自动 mip
生成。每次实际完成反射相机渲染后调用 `GenerateMips()`，再发布纹理供海面采样。
这遵循 Unity 对[手动生成 RenderTexture mip](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/RenderTexture.GenerateMips.html)
的要求。未增加反射相机、颜色 RT 数量或 Render Pass。

着色器同时考虑已有高光抗混叠得到的感知粗糙度 `p` 和反射纹理相对屏幕的像素尺寸。
粗糙度对应的近似过滤直径为 `max(width, height) * p² * 0.0625 * scale`；取它与
像素足迹、1 个纹素的最大值，再使用 `log2` 选择 mip，并限制在实际 mip 链内。
纹理尺寸和 mip 数使用固定版本 Unity Core 的查询接口；不支持 mip 数查询的分支
安全回退至 LOD 0。屏幕坐标保持 float，避免大分辨率下过早量化。

| 控制位置 | 参数 | 默认值 | 行为与代价 |
| --- | --- | --- | --- |
| OceanPlanarReflection 组件 | Use Mip Maps | `true` | 启用颜色 mip 存储和每次反射刷新后的生成；关闭可省去这两项成本 |
| Ocean 材质 | Planar Reflection Roughness Filtering | `1` | 范围 0–2；设为 0 关闭粗糙度造成的模糊，像素足迹过滤仍可生效，且不会停止 mip 生成 |
| Ocean 材质 | Planar Reflection Edge Fade | `0.02` | 按归一化 UV 到捕获边缘的距离淡出；设为 0 仍保留半个过滤纹素的安全过渡 |
| 原有组件设置 | Texture Size、HDR、Refresh Per Frames | 保留 | 控制捕获质量、格式与刷新频率；修改尺寸、HDR、Stencil 或 mip 设置时重建 RT |

捕获范围外使用已有环境反射。范围内按过滤覆盖率合成：普通 RGBA 捕获在透明黑
背景上过滤后，RGB 已包含覆盖率，因此使用
`environment * (1 - intensity * alpha) + capturedRGB * intensity`，不再对 capturedRGB
重复乘 alpha。alpha 小于等于 0 时不贡献 RGB，保留对特殊 Terrain alpha 的防护。
自定义捕获材质或天空着色器如使用特殊 alpha 语义，仍需单独观察。

### 生命周期修复

反射注册表的快速缓存会在同一相机更换纹理时更新。删除已销毁纹理的条目时也会正确
清除引用；RT 丢失原生资源后立即允许重建，不受普通刷新节流阻挡。渲染或 mip 生成
抛出异常时撤销该相机的纹理发布，避免继续采样部分更新的颜色／旧 mip；原有水体启用、
反面剔除及质量设置仍在 `finally` 中恢复。

### 成本与近似范围

完整二维 mip 链约增加 **33% 的反射颜色纹理内存**，不包括深度附件；每次反射刷新
增加一次 mip 生成。例如 256² ARGBHalf 的基础颜色约为 0.5 MiB，带完整 mip 约为
0.667 MiB。实际分配和 GPU 帧时需要 Profiler 确认。

这里是屏幕空间的普通 mip 过滤，**不是 GGX 角向预卷积**，无法解决屏幕外反射、
所有遮挡误差或现有单目 XR 平面反射限制。自定义 Cubemap 路径保持现有行为：普通
mip 不能被假定为镜面预卷积结果；Unity 的 Cubemap 导入也明确区分普通 mip 与
[Specular Convolution](https://docs.unity3d.com/Manual/texture-type-cubemap.html)。

## 2. 法线：让过滤足迹对应实际流动 UV

实现：[OceanNormalMapping.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanNormalMapping.hlsl)、
[Texture.hlsl](../crest/Assets/Crest/Crest/Shaders/ShaderLibrary/Texture.hlsl)、
[WaveNormals.png.meta](../crest/Assets/Crest/Crest/Textures/WaveNormals/WaveNormals.png.meta)。

法线采样现在先应用浮动原点与完整的空间变化流场位移，再对实际 UV 求 `ddx/ddy`。
梯度位于近远 LOD 的条件分支之前：近级两个滚动样本使用原梯度，远级两个样本使用
一半梯度，与远级两倍纹理世界周期一致。均匀滚动偏移不改变梯度。

Microsoft 的[显式梯度采样说明](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-tex2dgrad)
指出，相邻像素进入不同分支时，在分支内请求梯度存在歧义。本轮显式传入梯度，同时
保留实际采样 UV、滚动速度、流动周期、强度、LOD 混合和原有条件采样数量。

自带 WaveNormals 明确设为 Trilinear，并请求各向异性等级 4。这有助于掠射角过滤，
但可能增加硬件采样工作；实际效果受全局 QualitySettings 与后端影响，不能把等级 4
理解为固定执行 4 次纹理采样。

这项改动不恢复 mip 中丢失的法线方差。Unity 压缩法线解码会重建 Z，仅凭解码后的
法线长度无法可靠恢复原来的细节分布；进一步的方差预过滤需要额外统计数据和明确的
资产编码／导入流程，不能只在当前着色器中加一个长度补偿公式。

## 3. 浅水：正交缓存、float 高度链与烘焙

实现：[OceanDepthCache.cs](../crest/Assets/Crest/Crest/Scripts/LodData/OceanDepthCache.cs)、
[CopyDepthIntoCache.shader](../crest/Assets/Crest/Crest/Shaders/OceanInputs/Resources/CopyDepthIntoCache.shader)、
[OceanHelpersNew.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanHelpersNew.hlsl)
及其波浪、几何输入、泡沫、阴影和水下遮罩调用路径。

### 正交高度重建

缓存相机使用正交投影，设备深度在近远平面之间线性变化。现在直接在
`cameraY - near` 与 `cameraY - far` 之间插值，反向 Z 时先执行 `1 - depth`，最后
只减去一次相对高度偏移。旧路径的远平面高度多减了一次 near，组合旧自定义 Z
公式后，普通 Z 路径会产生近裁剪距离大小的地形高度偏差。

独立正交投影矩阵回归覆盖两种 clip 深度约定及各自的普通／反向 Z。近裁剪面
0.3 m、岸坡 1:20 的数值例子中，旧普通 Z 路径的高度误差对应 **6 m 岸线偏移**；
新公式消除了这个数学偏差。原反向 Z 路径存在误差抵消，因此不能将上述 6 m 结论
泛化到全部后端；这些检查也不意味着项目恢复了 OpenGL／WebGL 支持。

### 高度先相减，再降低精度

地形高度和水位偏移先以 float 采样、相减，之后才把较小的局部水深交给原有 half
输出。这个精度链涵盖波浪浅水衰减、泡沫、水位基础法线、主水面／水下 Mask／Alpha、
五个几何与裁剪输入，以及阴影的地形比较。这些高程类型调整保留现有模拟纹理格式。

| 数值例子 | 旧路径的损失 | 新路径 |
| --- | --- | --- |
| 水位 1024 m，地形 1023.75 m，真实水深 0.25 m | 绝对高度先转 binary16，水深可能归零 | 先以 float 相减，保留 0.25 m |
| 平水位偏移 1024.25 m，相邻样本间距 0.25 m | 中心 half、邻点 float，为平面算出 1.0 的虚假坡度 | 样本精度一致，坡度为 0 |
| 烘焙高度 100.015625 m | RGBAHalf 读回先量化为 100.0，再写 float EXR 也无法恢复 | RFloat 读回保留原 float 样本 |

这些例子模拟 IEEE binary16／binary32；桌面后端可能将 `half` 提升为 float，因此
不是对某款 GPU 的实测结论。已有 R16 源数据的量化无法恢复，默认变化水位使用的
`R32G32_SFloat`（每通道 32 位浮点）数据提供足够的源精度；本轮也不构成任意大世界坐标的完整精度保证。

### 缓存文件与失败恢复

烘焙现在使用线性 `RFloat` CPU 纹理读回，再以 `OutputAsFloat` 写 EXR。Unity
[EXR API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ImageConversion.EncodeToEXR.html)
支持单通道 RFloat 灰度输出。导入器默认最大尺寸提升到足以容纳捕获分辨率，避免
3072² 等缓存被旧默认 2048 上限缩小；用户显式的平台覆盖设置仍然生效。

RT 显式设置 Clamp，深度捕获使用 Point，高度结果使用 Bilinear。未把旧代码没有
显式设置 wrapMode 宣称为已经证实的 Repeat 错误，原生默认值尚未在 Unity 中核实。
重建／销毁时回收 RT、辅助材质和临时读回纹理，解除相机 targetTexture 引用；
Blit 与读回通过 `finally` 恢复原来的 `RenderTexture.active`。

保存前必须成功生成本次缓存。旧 absolute 烘焙缓存迁移为 relative 时，若捕获、
读回、编码或文件写入失败，会恢复旧解释标记及 quad 状态；若 Realtime 已经生成
新的 relative RT，则保留与该 RT 一致的标记。这不是原子文件写入或完整的 Unity
导入事务，不能承诺任意磁盘／导入失败都没有副作用。

## 4. 水下面：统一 Fresnel 与全反射

实现：[OceanReflection.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanReflection.hlsl)
的 `ApplyReflectionUnderwater`。

原来透射用重建的空气侧角度计算 Fresnel，内部反射却使用水侧观察角度，两个权重
不互补。现在按 Snell 关系计算空气侧角度，两项共享 `R`，合成为
`transmitted * (1 - R) + underwaterReflection * R`。超过临界角时 Fresnel 为 1，
再应用原有的反射权重；点积与开方输入显式限界，移除了易受舍入影响的嵌套反三角函数。
物理依据见 [PBRT 的 Snell／全反射与 Fresnel 推导](https://www.pbr-book.org/4ed/Reflection_Models/Specular_Reflection_and_Transmission)。

以空气折射率 1、水 1.333、Fresnel Power 5、反射权重 1，上下两项输入都为白色 1
计算实际源码表达式：

| 水中观察方向与法线夹角 | 旧合成亮度 | 新合成亮度 |
| --- | ---: | ---: |
| 0° | 1.000000 | 1.000000 |
| 40° | 0.974557 | 1.000000 |
| 临界角约 48.60663° | 0.024745 | 1.000000 |
| 60° | 0.050987 | 1.000000 |
| 80° | 0.397846 | 1.000000 |

这是合成权重的数值回归，不是画面亮度测量。保留现有可调 Schlick／Fresnel Power、
散射颜色和反射来源，不将结果称为完整能量守恒 BRDF 或真实水下场景反射。
先前被错误压暗的区域可能明显变亮，需要结合已有散射材质重新观察。

## 5. 验证与复现

| 检查 | 本轮结果 |
| --- | --- |
| Shader 源码／元数据 | 101 个 Shader/HLSL/Compute 文件、186 个本地 include、6 个程序集定义与 manifest、15 个既有新增资源检查条目；0 错误 |
| 离线 DXC | **173 / 173 通过**；基线 `a61488b9` 使用同一扩展用例集也为 173 / 173 |
| 既有回归 | URP 9、高光 6、泡沫 8、光学 9；共 **32 / 32** |
| 本轮新增回归 | 法线梯度 **6 / 6**、深度缓存 **11 / 11**、反射 **14 / 14**；共 **31 / 31** |
| C# 9 语法 | 146 个文件 × 4 组 Unity Editor／Player 符号配置；**0 错误**，未绑定 Unity 程序集 |
| 差异检查 | `git diff --check` 通过 |
| Unity／GPU／Player／Android | 本轮未运行 |

DXC 用例从上一轮 117 个扩展到 173 个，补入平面反射／流场／立体相关变体、深度缓存、
波谱与 Gerstner 输入、几何高度／裁剪输入、OceanSurfaceAlpha、水下 Mask 与阴影更新。
编译警告共 229 条，与基线数量和各案例分布一致；“通过”指没有编译错误。

检查使用真实 Unity 头文件：Unity Graphics 提交
[`a7e4c051`](https://github.com/Unity-Technologies/Graphics/tree/a7e4c051d256a781ab362c64316b125a1e104694)
中的 Core／Universal／Universal Config 17.6，以及 Unity 6000.6.2f1 官方
Built-in CGIncludes。DXC 为官方 `v1.9.2609`，C# 语法工具使用 .NET 10.0.100 Roslyn。
DXC 以 D3D11 预处理分支生成 SM6 DXIL；不等价于 Unity Shader 导入、实际 D3D11
驱动编译、Metal／Vulkan 后端、Player 变体裁剪或移动 GPU 半精度执行。

在仓库根目录运行：

```bash
python tools/check_urp.py
python tools/check_urp_regressions.py -v
python tools/check_specular_quality.py -v
python tools/check_foam_quality.py -v
python tools/check_optical_quality.py -v
python tools/check_normal_sampling.py -v
python tools/check_depth_cache_quality.py -v
python tools/check_reflection_quality.py -v
dotnet run --project tools/StaticChecks -- crest/Assets
git diff --check

python tools/check_urp_shaders.py \
  --dxc /path/to/dxc \
  --urp-headers /path/to/unity-graphics \
  --unity-includes /path/to/Unity/CGIncludes \
  --output /path/to/crest-shader-checks
```

`--urp-headers` 指向包含 `Packages` 的目录，需要 Core、Universal 和 Universal Config
头文件。给离线编译或新增数值工具增加 `--ref a61488b9` 可读取基线源码，不替换工作区。
新增测试检查独立的投影、过滤足迹、覆盖合成、Fresnel 权重与浮点精度不变量，并包含
必要的生命周期源码约束；没有执行原生 RT 销毁、EXR 导入或采样器硬件行为。

## 6. 本地画面与性能验收

使用 `main`、`BoatWakes`、`LakesAndRivers` 和受控浅水场景；固定曝光、时间、相机与
材质后比较，再录制缓慢移动的相机序列。既有自动工具的使用步骤见
[UnityQualityTests](../tools/UnityQualityTests/README.md)，下列新增项目需要人工补充：

1. **平面反射清晰度与覆盖**：细立柱、船体和岸边物体，逐步增加粗糙度；切换 Use Mip Maps、
   Roughness Filtering 0／1，观察远处闪烁、过度模糊、透明黑边和捕获范围外的过渡。
2. **反射更新**：切换 HDR、尺寸、Stencil、mip 与刷新间隔，停用／启用组件，制造 RT 资源丢失后
   恢复；交替查看 Game／Scene View，检查旧纹理、黑帧和异常状态残留。
3. **法线与流场**：贴水低视角缓慢跨越 LOD，开启空间变化的流场；比较远场跳闪、接缝和近处细浪。
   分别记录默认各向异性设置与全局关闭／强制设置的效果。
4. **高程与浅水**：检查零基准、高海拔以及局部水位偏移 1024.25 m 的 `R32G32_SFloat` 测试场景，布置
   厘米级浅水和缓坡；核对可见水面、Mask、Alpha、裁剪、岸边泡沫及阴影的一致性。
5. **缓存往返**：对比实时缓存与新烘焙 EXR，包括大于 2048 的分辨率；检查导入后的尺寸、通道、
   精度和相对高度。验证旧 absolute 缓存升级及受控的捕获／读回失败恢复。
6. **水下面观察**：关闭干扰光源，检查 40°、临界角附近及 60°–80° 视角；穿越水面，复核
   Snell 窗口、亮度变化和两种全屏水下路径，按需要调整既有散射参数。
7. **目标设备成本**：Windows 与 Android Vulkan 分别运行 Development Player，记录反射刷新帧
   与普通帧的 GPU 时间、RT 内存、纹理采样成本、变体编译和裁剪；XR、Metal 等按项目目标追加。

## 7. 后续较大工作

这轮完成了上一份报告中平面反射过滤和岸边缓存精度的具体部分。下列能力仍应以独立
实现与场景对照验收推进：

| 工作 | 需要补齐的内容 |
| --- | --- |
| 法线方差资产预过滤 | 存储 mip 内法线统计信息，与压缩／导入／采样约定一起设计，验证远场高光与近场细节 |
| 更准确的粗糙反射 | 角向 GGX 预卷积或其他经过参考图像评估的近似；明确自定义 Cubemap 的内容约定 |
| 局部海况和入岸波 | 根据水深、坡度、岸线方向配置波谱，处理浅水破碎波；需要真实尺度与浪向对照 |
| 船艏浪、尾迹与喷雾 | 船型／速度相关的生成与泡沫寿命协同，并量测粒子、透明过绘和模拟成本 |
| SSR 与回退 | 屏幕外、遮挡、时域稳定性及探针／平面反射混合，需要额外资源和 GPU 预算 |

研究使用公开官方文档、公开物理推导及仓库现有 MIT 源码，没有引入商业 Crest 包实现。
