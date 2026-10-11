# URP 海面真实度改进：高光、泡沫与水体光学

日期：2026-10-11。分支：`urp`。本轮基线：
[`2aaeec77`](https://github.com/nx111/crest/commit/2aaeec77441f6a616baada1a38e72f02e54d07a5)。
目标版本保持 **Unity 6000.6.2f1 / URP 17.6.0**。

本轮已实施高光抗闪烁、泡沫时间积分与远场采样、折射光程和焦散修正。
当前结果来自源码审查、实际 HLSL 的离线编译和数值验证；**本轮没有运行 Unity Editor、
GPU 截图比较或 Android 真机测试**。之前的 D3D11 截图和 Built-in 对照属于
[上一轮质量报告](URP-QUALITY-AUDIT.md)，不能作为这些新改动的画面验收。

## 研究结论与选择

当前分支已经补齐 URP Render Graph 调度、点光／聚光灯、选中透明图层折射、
探针混合／盒投影和阴影细节。继续提高真实度，优先级较高的是让现有波浪的
高光、泡沫和透射在距离、视角、时间步变化时保持合理，而不是增加一层无条件白沫。

| 已定位的问题 | 本轮处理 | 画面上的预期作用 |
| --- | --- | --- |
| 探针固定粗糙度；太阳与局部灯使用最高 4096 的窄 Phong 高光，没有考虑一个像素内的法线变化 | 用法线导数估计方差，增加探针过滤粗糙度，并展宽／降低直射高光峰值 | 减少远处浪面亮点的跳闪，保留近处解析度足够的细浪 |
| 泡沫使用 Euler 衰减，较大时间步会改变寿命；零衰减预热存在除零 | 衰减与内建生成量使用同一步的解析积分，零衰减只累计真实时间 | 减少改变模拟频率时尾迹和白浪寿命的变化 |
| 水下气泡固定读取 mip 3，掠射视差分母可趋零 | mip 至少覆盖当前像素足迹；限制掠射视差；白沫边缘使用最小像素羽化 | 减少地平线气泡混叠、突然拉长的纹理及白沫边缘闪烁 |
| 折射选择新 UV 后，焦散仍沿原像素视线重建；吸收只计算相机 Z 深度差 | 保持颜色、深度和接收点配对，使用水中段长；正交相机使用正确的平行视线 | 斜看浅水的吸收更合理，焦散更贴合被折射的水底接收点 |
| 太阳焦散投影除以光源竖直分量，水平光会发散；干燥表面和夜间缺少可靠门控 | Snell 方向投影、入水距离衰减、日照／浸没门控、非负调制 | 日落附近焦散位置更稳定，深处和无直射光处自然变弱 |
| 旧 Curtain 路径仍引用 Built-in 光源变量 | 改为分支已有的 URP 光源助手，统一深度和视线处理 | 修复该旧路径的离线 HLSL 编译错误 |

## 1. 高光过滤与材质控制

实现：[OceanSpecular.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanSpecular.hlsl)、
[OceanReflection.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanReflection.hlsl)、
[OceanAdditionalLights.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanAdditionalLights.hlsl)。

在最终水面法线归一化后、翻转水下面法线前计算一次像素法线方差，并把标量传入
反射和灯光循环。这样不在水上／水下分支或逐灯循环内调用屏幕导数。

探针参数是感知粗糙度 `p`，其物理粗糙度 `alpha = p²`。过滤使用
`p_filtered = (p⁴ + 2 * variance)^(1/4)`，并限制额外方差和最终范围。
这与 Unity Core `NormalFiltering` 的参数映射一致，避免把方差错误加到 `p²`。

原有 Phong 高光保留，使用小角近似 `n_filtered = n / (1 + 4 * variance * n)` 展宽。
峰值同时乘以 `(n_filtered + 1) / (n + 1)`，保持这个角向 lobe 的积分。
这不是完整的能量守恒水体 BRDF：已有 Fresnel、太阳地平线修补和局部灯 `NdotL`
仍然参与最终合成。

| Ocean 材质新增参数 | 默认值 | 用法 |
| --- | --- | --- |
| Specular Anti-Aliasing Variance | `0.15` | 控制像素法线变化带来的过滤；设为 `0` 恢复未过滤高光 |
| Specular Anti-Aliasing Threshold | `0.2` | 限制额外粗糙度，避免强变化处过度展宽；设为 `0` 同样关闭过滤 |

探针反射、太阳和局部灯高光接受本次过滤。自定义 Cubemap 和平面反射仍保留各自
采样路径，未新增预过滤资源；泡沫太阳高光也未改为完整独立的抗混叠 BRDF。
导数方差不包含法线纹理 mip 中丢失的全部方差信息，不能保证消除所有远场闪烁。

## 2. 泡沫生成、消退与采样

实现：[UpdateFoam.compute](../crest/Assets/Crest/Crest/Shaders/Resources/UpdateFoam.compute)、
[OceanFoam.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanFoam.hlsl)。

对每一步内的常量生成率 `S` 和衰减率 `k`，采用：

```text
foam_next = foam_previous * exp(-k * dt) + S * (1 - exp(-k * dt)) / k
k = 0 时：foam_next = foam_previous + S * dt
```

小指数用级数避免 `1 - exp(-x)` 的消减误差；计算保持 float，再写入既有模拟纹理。
预热保留原来较弱的波峰泡沫策略，但零衰减时没有有限稳态，因此只累计实际时间。
极小正衰减的预热时间上限为 10000 秒，避免无界倒数。

从基线实际 Euler 表达式计算，`k = 0.8/s`、初值 1、无新生成时，5 秒后的结果为：

| 模拟频率 | 原表达式 | 指数解析值 | 原相对误差 |
| --- | ---: | ---: | ---: |
| 15 Hz | 0.016397642 | 0.018315639 | -10.47% |
| 30 Hz | 0.017347604 | 0.018315639 | -5.29% |
| 200 Hz | 0.018169310 | 0.018315639 | -0.80% |

新表达式的数值回归与解析解一致。这个结论适用于衰减和内建波峰／岸线生成项；
流动重采样、R16 量化和单独绘制的自定义泡沫输入仍保留原有离散流程。

白沫羽化保留原来的起始阈值，确保模拟量为零时不会出现白沫；仅在像素变化超过
材质羽化宽度时增宽。这可能轻微减少远场覆盖，不是严格的亚像素覆盖率积分。
气泡使用 `max(3, 像素足迹对应 mip)`，保持近处的原有模糊程度，纹理采样次数不增加。

波浪最小采样切片在 CPU 和 GPU 两端限制到实际 LOD 范围。
FFT／Gerstner 波谱、位移、浮力查询、Jacobian 差分 stencil、默认泡沫覆盖率与生成强度
均保留既有定义。

## 3. 折射、吸收和焦散

实现：[OceanEmission.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanEmission.hlsl)、
[OceanShaderHelpers.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanShaderHelpers.hlsl)
及各水下调用路径。

折射 UV 限制在有效屏幕范围内；采到水面前方物体时，颜色 UV 和深度 UV 一起回退。
MSAA 深度处理后保留相同的 UV／深度配对。用于透射吸收的距离来自接收点和水面点
之间的段长，而不再仅是两者的眼空间 Z 差；浅水斜视时，穿水距离较长的光会有更强吸收。

光程计算使用视空间端点，避免绝对世界坐标相减导致薄水层在大世界中丢失精度。
只有焦散接收点需要转换到世界空间。天空没有有效接收表面，使用有限的相机远裁剪
距离估计雾，方向重建使用安全深度，避免无穷远点与零吸收系数相乘产生 NaN。
全屏水下、提前水下合成、透明物体 Shader API 和旧 Curtain 路径统一换算视线段长。

焦散使用现有空气／水折射率构造进入水中的太阳方向；太阳接近地平线时平滑淡出，
不再直接除以接近零的太阳高度。接收点在水面之上、日照关闭、纹理尺度无效时跳过。
入射光沿水中段进行各颜色通道的指数衰减，并限制最终颜色调制不小于零。
分支内纹理查询使用显式 LOD。

这些改变可能让原来偏强、偏浅或方向不准确的焦散明显变淡或移动，需要本地观察后
微调现有 `Depth Fog Density`、`Caustics Strength` 和焦距参数。
透射仍然使用屏幕空间法线偏移；所求段长是水面到被采样接收点的近似直线段，
没有新增多次折射、屏幕外场景查询或逐像素透明物体深度。

## 4. 验证与复现

运行根目录中的检查：

```bash
python tools/check_urp.py
python tools/check_urp_regressions.py -v
python tools/check_specular_quality.py -v
python tools/check_foam_quality.py -v
python tools/check_optical_quality.py -v
dotnet run --project tools/StaticChecks -- crest/Assets
dotnet run --project tools/StaticChecks -- tools/UnityQualityTests
git diff --check
```

离线 HLSL 编译使用新增的 [check_urp_shaders.py](../tools/check_urp_shaders.py)：

```bash
python tools/check_urp_shaders.py \
  --dxc /path/to/dxc \
  --urp-headers /path/to/urp-headers \
  --unity-includes /path/to/Unity/CGIncludes \
  --output /path/to/crest-shader-checks
```

`urp-headers` 应包含实际 `Packages/com.unity.render-pipelines.*` 目录；检查不使用
自行编造的 Unity API 桩。本轮采用 Unity Graphics 提交
[`a7e4c051`](https://github.com/Unity-Technologies/Graphics/tree/a7e4c051d256a781ab362c64316b125a1e104694)
的 URP 17.6 头文件及 Unity 6000.6.2f1 官方 Built-in shader headers。
DXC 以 SM6 DXIL 检查 HLSL，使用对应的 D3D11 预处理分支；它不等价于 Unity 的
完整 Shader 导入、实际 D3D11 驱动编译、移动 GPU 精度或 Player 变体裁剪。

增加 `--ref 2aaeec77` 可重现基线检查，工具会提取完整 Shader 源码快照，避免基线
意外包含工作区的新头文件。基线 117 个选定阶段／变体中有 4 个旧 Curtain 阶段因
`_WorldSpaceLightPos0` 未声明而失败；本轮已经修复。

本轮最终检查结果：

| 检查 | 结果与范围 |
| --- | --- |
| Shader 源码／元数据 | 101 个 Shader/HLSL/Compute 文件、186 个本地 include、6 个程序集 JSON、15 个新资源检查条目；0 错误 |
| 离线 DXC | **117 / 117 通过**；海面、6 个水下 Pass、Curtain、示例水下材质及泡沫 Compute 的选定阶段／变体 |
| URP 回归 | **9 / 9 通过**；含 1458 个透视／正交、不同深度约定、旋转和大世界坐标的接收点重建案例 |
| 水体光学 | **9 / 9 通过**；视线距离、UV／深度配对、有限天空、Snell 方向和日照／浸没门控、非负焦散 |
| 薄水层单精度 | **72 个 IEEE float32 案例通过**；2 mm–10 cm 光程、0／80 km／1000 km／10000 km 世界平移，所测视空间长度误差不超过 0.01 mm；不代表整个渲染器可在这些绝对坐标精确运行 |
| 泡沫 | **8 / 8 通过**；15／30／60／200 Hz、衰减 0–20、暂停／预热、LOD／mip 和视差边界 |
| 高光 | **6 / 6 通过**；零方差保持、粗糙度单调有界、高光峰值和角向积分、导数调用位置 |
| C# 9 语法 | 146 个项目 C# 文件 × 4 个 Unity Editor／Player 符号配置，另加 QualityRunner × 4；均为 0 错误。使用 .NET 10.0.100 Roslyn，未绑定 Unity 程序集 |
| 差异检查 | `git diff --check` 通过 |
| Unity／GPU／Player／Android | 本轮未运行 |

共 **32 项数值／源码回归通过**。除专门的 float32 案例外，数值检查使用 CPU 双精度
执行实际 Shader 表达式，不代表 GPU 半精度效果。高光、泡沫和光学测试检查独立的
几何、积分或取值范围不变量，而不是把新的代码输出硬编码成预期图像。

离线编译仍有 147 条警告，数量与各案例分布均与基线一致，主要来自 DXC 的旧兼容
选项及既有向量截断；这里的“通过”指零编译错误，不表示零警告。

反向验证：重建数值测试在 `2aaeec77` 的首个折射偏移案例出现 5.2 cm 误差并失败；
非负焦散测试也会在该基线失败。这两项均在本轮源码上通过。

新增的可选 Unity 高光诊断步骤见
[UnityQualityTests](../tools/UnityQualityTests/README.md)：使用 `-crestSpecularAA`
产生关闭／开启／再次关闭过滤的图像，并检查 HDR 像素有限性。此前 Built-in 对照
会明确关闭新过滤功能，以比较同一组着色选项。新增诊断本轮尚未实际运行。

## 5. 本地画面验收与后续优先级

建议先在 `main`、`BoatWakes` 和 `LakesAndRivers` 三个场景查看：

1. 第三人称低视角缓慢转动相机，对比高光过滤 0 与 0.15；检查远处闪烁、近处细浪和过度展宽。
2. 同一船速下切换泡沫 15／30／60 Hz，对比尾迹寿命；再测试零衰减、暂停、瞬移和减少 LOD 数。
3. 浅滩、水下几何和船体交界处，使用窄／宽 FOV 及正交相机；观察折射轮廓、水深吸收、焦散贴合。
4. 在较大世界坐标处复查厘米级浅水；分别以太阳高悬、低于地平线和关闭主光观察焦散。
5. 缓慢穿越水面，并切换提前／晚期水下、MSAA 和 Render Scale；检查接缝及双重施雾。
6. Windows Development Player 与 Android Vulkan 分别验证编译、帧时、纹理内存和变体裁剪。

本轮没有新增 Render Pass、RT、模拟层或纹理资源。增加的成本主要是像素导数、
粗糙度／光学数学运算、每像素光程重建，以及每个泡沫模拟单元的指数运算。
现有纹理采样次数整体保持；是否满足目标设备帧时仍须用 Profiler 测量。

后续更大的改进应在有实景对照和性能预算后分开实施：

| 优先级 | 后续工作 | 为什么需要独立验收 |
| --- | --- | --- |
| 高 | 岸边深度缓存精度、局部波谱与波浪入岸方向，形成具体海况预设 | 依赖真实地形尺度和浪向，不能只增大所有波浪陡度或全域泡沫 |
| 高 | 法线纹理预过滤方差、平面反射的距离／粗糙度过滤 | 本轮导数近似不能恢复 mip 内全部细节方差，平面反射仍用现有采样 |
| 中 | 船艏浪、Kelvin 尾迹、破浪喷雾与泡沫寿命协同 | 需要船速、船型、风场和粒子性能实测，避免持续白色覆盖 |
| 中 | SSR 与探针／平面反射的回退策略 | 涉及额外屏幕资源、遮挡和时域稳定性；官方 URP SSR 文档当前仍标为预览 |
| 按场景 | 专用浅水破碎波和水下反射 | 属于新的模拟／渲染能力，额外 GPU、内存与带宽成本明显 |

## 参考依据

- [Unity Core CommonMaterial.hlsl](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl)：`GeometricNormalVariance`、`NormalFiltering` 与感知粗糙度映射。
- [Tokuyoshi / Kaplanyan，JCGT 2021](https://www.jcgt.org/published/0010/02/02/paper.pdf)：几何和法线引起的高光混叠与方差过滤。本文的 Phong 小角公式是针对旧 Crest lobe 的近似推导，未声称移植整篇论文算法。
- [Unity URP：从深度重建世界坐标](https://docs.unity3d.com/6000.0/Documentation/Manual/urp/writing-shaders-urp-reconstruct-world-position.html)：深度范围、逆投影和屏幕 UV 的匹配。
- [PBRT：Media](https://www.pbr-book.org/3ed-2018/Volume_Scattering/Media)：沿真实介质段的透射率和均匀介质指数吸收。
- [Crest 官方 Foam](https://docs.crest.waveharmonic.com/Manual/Appearance/Foam.html)：波峰挤压、岸边生成、衰减率与纹理采样的作用。
- [Crest 官方 Reflections](https://docs.crest.waveharmonic.com/Manual/Appearance/Reflections.html)：探针／平面反射和 URP SSR 的范围、配置与成本。

研究仅使用公开文档、公开论文和现有 MIT 源码；没有复制商业 Crest 包的实现。
