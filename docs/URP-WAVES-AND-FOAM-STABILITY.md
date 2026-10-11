# URP 真实度改进（三）：浪面法线、波浪时序与泡沫稳定性

日期：2026-10-11。分支：`urp`。本轮基线：
[`8b56660e`](https://github.com/nx111/crest/commit/8b56660ee51d55db50e36e6534d758a4cd8c34a8)。
目标版本保持 **Unity 6000.6.2f1 / URP 17.6.0**。

本轮接续[反射过滤、法线采样与浅水精度](URP-REFLECTIONS-AND-SHALLOW-WATER.md)，
已实施浪面几何法线修正、FFT 循环谱与时序修正、Gerstner 相位精度提升、FFT 烘焙读回
修复，以及泡沫的输运、输入和着色改进。最终验证为 **197 / 197 个选定离线着色器
编译用例、105 / 105 项数值／源码回归通过**；146 个 C# 文件在 4 组条件编译配置
下语法检查无错误。

**本轮没有运行 Unity Editor、GPU 画面对照、Player 或 Android 真机检查。**
历史 D3D11 画面记录属于 `2aaeec77`，不能作为之后三批修改的视觉验收。下文的数值
来自源码表达式的 CPU 检查，画面改善是据问题机制推导的预期效果。

升级后，已有 **FFT 碰撞烘焙资产需要重新烘焙**，才能与修正后的循环谱振幅和时间
缩放一致。默认 30 Hz 的内置泡沫输入单次输出保持原值；使用其他模拟频率时，输入
强度现在按同一参考速率归一化，应复核原先针对旧行为调整过的尾迹材质。

## 研究结论与实施范围

| 定位的问题 | 已实施处理 | 预期改善 |
| --- | --- | --- |
| 几何法线先单位化、丢弃 Y 后又以 Y=1 合成，把陡浪的坡度压小 | 累加与调用方一致的坡度；中心和邻域样本统一 float；退化保护不随网格面积变化 | 陡浪反光方向更贴合几何，减少精度引入的假坡度 |
| 循环频率量化同时影响初始谱能量；非整数 Time Scale 与烘焙周期不一致 | 初始谱保留自然色散，循环量化仅用于相位；统一时间域 | 同一海况不因循环时长改变初始振幅，循环边界更一致 |
| Gerstner 波矢和相位过早使用 half | 波矢、相位及三角函数计算保留 float | 减少长时间运行停顿和周期接缝的精度来源 |
| 泡沫 Euler 回溯偏离弯曲水流，跨多级 LOD 时可能丢失历史；输入每步固定添加 | 中点回溯、完整祖先查询、按实际子步归一化三类内置输入 | 尾迹更贴合弯曲流场，模拟频率与输入浓度解耦 |
| 白浪过滤同时改变岸边定位；LOD 阈值和 Flow 颜色混合顺序有误 | 岸边使用完整位移；先求覆盖再混合；颜色按覆盖率加权 | 降低岸边漂移、LOD 中途白沫消失和流动相位染色 |
| 3D 白沫太阳高光未使用自身法线过滤，背面光也可能产生高光 | 复用现有 Specular AA，并乘局部泡沫法线的 N·L | 减少泡沫细小高光的闪烁来源和背光亮斑 |

## 1. 浪面法线：保留坡度再合成

实现：[OceanHelpersNew.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanHelpersNew.hlsl)、
[Ocean.shader](../crest/Assets/Crest/Crest/Shaders/Ocean.shader)。

原调用方从 `(0, 1, 0)` 开始累加法线 XZ，再统一归一化；被调用函数却返回单位几何
法线的 XZ。对于倾角为 θ 的单坡面，这相当于把 `tan(θ)` 换成 `sin(θ)`，所以
反光方向比实际浪面更平。以关闭细节法线、整体法线强度为 1 的 60° 单坡面为例，
旧路径得到约 **40.893°** 的着色倾角，新路径恢复 **60°**。

现在先由位移差构造每世界单位的两条切向，再求 `cross(tangentZ, tangentX)`。
传回的坡度为 `cross.xz / max(cross.y, 0.0001)`，与调用方隐含的 Y=1 一致。
水位、波浪和原有细节贡献合成后，继续应用已有整体强度与最终归一化。
[Blending in Detail 的偏导混合讨论](https://blog.selfshadow.com/publications/blending-in-detail/)
说明了从法线恢复坡度后再混合的依据；本轮没有实施其中的 RNM 细节法线重定向。

先用 texel size 归一切向，使 `0.0001` 保护对应无量纲的水平 Jacobian，不再随 LOD
网格单元面积变化。中心样本从 half 改为 float，与邻域采样一致，避免同一个常量
位移因不同的存储舍入被解释成坡度。位移纹理采样次数保持原样。

准确性范围是非退化、正向的单 LOD 仿射曲面，以及不含水平 chop 的线性高度场混合。
`cross.y < 0.0001` 时仍使用人为的向上退化保护，不再保证法线精确垂直切向。
包含水平 chop、变化 LOD 权重和细节法线的完整海面仍是近似合成；已有 R16 源数据
丢失的精度也不能恢复。较陡波浪的反光会明显改变，需要检查原有材质的整体法线强度。

## 2. 波浪：分离谱能量、循环相位和存储精度

实现：[FFTSpectrum.compute](../crest/Assets/Crest/Crest/Shaders/Resources/FFT/FFTSpectrum.compute)、
[FFTCompute.cs](../crest/Assets/Crest/Crest/Scripts/Shapes/FFT/FFTCompute.cs)、
[Gerstner.compute](../crest/Assets/Crest/Crest/Shaders/Resources/Gerstner.compute)、
[GerstnerShared.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanInputs/GerstnerShared.hlsl)、
[FFTBaker.cs](../crest/Assets/Crest/Crest/Scripts/Shapes/FFT/FFTBaker.cs)。

### 初始谱使用自然色散，循环只修改相位

深水色散保持 `ω = sqrt(gk)`，频率到波数的导数保持 `dω/dk = g/(2ω)`。
旧代码在同一个 helper 中先按循环时长量化 ω，再用量化后的值计算谱密度及导数，
导致设置动画循环时长也改变初始海况。现在初始谱只使用自然频率及对应导数；更新
相位时再使用 `ceil(ωT / 2π) * 2π / T`。保留原来的向上取整选择。

[Tessendorf《Simulating Ocean Water》§4.2](https://www.researchgate.net/profile/Jerry-Tessendorf-2/publication/264839743_Simulating_Ocean_Water/links/5ba8ecb0a6fdccd3cb6f6ffd/Simulating-Ocean-Water.pdf?origin=publication_detail)
分别讨论深水色散与把时间频率限制为 `2π/T` 整数倍的循环方法。本轮据此区分动画
周期和初始谱的职责，未调整旧方向分布的归一化常数、随机种子或海况预设。

源码数值例：重力 9.81 m/s²、波长 128 m、风速 10 m/s、循环 32 s 时，旧初始化
路径使该波分量振幅成为自然谱的约 **1.67358 倍**；新路径的初始振幅不再依赖循环
时长。这是单个波分量的例子，不表示整个海面浪高都按同一比例变化。

零波数、零／负重力和零／负风速的初始化安全返回零；频率为零时导数返回零，避免
进入谱公式的除零项。常规非循环、正重力和正风速的谱设置保持原有定义。

### Time Scale 与烘焙周期使用一致的时间域

FFT 着色器接收的时间为 `time * timeScale`，因此循环周期也变为
`loopPeriod * abs(timeScale)`；非循环时沿用原值。烘焙与 CPU 查询继续使用未缩放
的 `loopPeriod`。这样 0.5、1.3 等非整数倍率也能在指定的外部周期首尾闭合。
零倍率冻结相位；脚本传入负倍率时保留反向传播。序列化字段名仍是 `_gravityScale`，
对应现有 Inspector 的 Time Scale，并未改成重力平方根缩放。

周期频率仍是近似色散，短周期下相速误差更明显。旧烘焙资产不会自动迁移，需重新
烘焙后核对画面和 CPU 波浪查询；离线检查只证明公式周期性，不替代 GPU/CPU 对照。

### Gerstner 相位保持 float

现代 Compute 与旧批量 Shader 两条路径都保留 float 波矢、方向和相位。现代路径的
`sincos` 输出也保持 float，之后才将有界位移结果交给原有 half 中间存储。现代
StructuredBuffer 的数据结构与波浪纹理格式保持原样，没有新增资源。

以 1 m 波长的正向第一相位、原点取样、初相位 0.7 rad、60 fps 连续取 60 帧为例，
按真实声明进行 IEEE binary16 舍入，
旧代码运行至 600 s／3600 s 时只有 **3／2 个不同相位值**；新 float 路径均保留
**60 个不同值**。这是存储精度诊断，其他算术并非完整 GPU 指令仿真。
Unity [16 位精度说明](https://docs.unity3d.com/6000.0/Documentation/Manual/SL-Use16BitPrecisionInShaders.html)
明确指出不足的精度可能造成几何停顿，且部分平台会把 half 提升为 float；因此不能
据此宣称所有桌面 GPU 原先都会发生相同停顿。

这项修改减少半精度相位的误差来源，不保证任意长时间或任意大坐标仍无精度损失。
支持原生半精度的设备可能增加寄存器／算术成本，需要在目标 Vulkan 设备上测量。

### FFT 烘焙格式与临时资源

最终碰撞资产本来就保存 half。现在临时渲染目标使用线性 ARGBHalf，与线性
RGBAHalf CPU 纹理一致，避免依赖 ARGBFloat 到 RGBAHalf 的跨格式 `ReadPixels`
转换。Unity [ReadPixels 文档](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Texture2D.ReadPixels.html)
要求源与目标格式匹配，并由设备支持渲染与采样。此次修改保留资产通道和最终精度；
无 mip 的 staging 纹理读回时显式关闭 mip 重算。

捕获之前保存 `RenderTexture.active`；无论正常完成还是异常退出，都在 `finally`
中恢复它并释放本地 CommandBuffer、临时 RT 与 CPU 纹理。这里只整理当前烘焙的
临时资源，不改变 FFT 的共享缓存生命周期，也没有实现整个资产保存流程的原子事务。
目标后端的 half UAV 写入和实际读回仍需在 Unity 中确认。

## 3. 泡沫动态：准确搬运、独立岸边定位和输入速率

实现：[UpdateFoam.compute](../crest/Assets/Crest/Crest/Shaders/Resources/UpdateFoam.compute)、
[LodDataMgrFoam.cs](../crest/Assets/Crest/Crest/Scripts/LodData/LodDataMgrFoam.cs)、
[SimSettingsFoam.cs](../crest/Assets/Crest/Crest/Scripts/LodData/Settings/SimSettingsFoam.cs)
以及 Texture、Vertex Colors、Spline 三类内置 additive 输入 Shader。

### 弯曲流场使用中点回溯

回溯位置从一次 Euler 取样改为：

```text
midpoint = position - 0.5 * deltaTime * velocity(position)
source   = position - deltaTime * velocity(midpoint)
```

[GPU Gems 第 38 章](https://developer.nvidia.com/gpugems/gpugems/part-vi-beyond-triangles/chapter-38-fast-fluid-dynamics-simulation-gpu)
说明了反向追踪、纹理重采样和更高精度的中点轨迹方法。本实现只改善既有速度场中的
泡沫搬运，没有增加完整的流体求解器。

中点速度查询使用当前帧的级联参数，历史泡沫使用源帧参数；两者沿祖先 LOD 查找
最近的有效覆盖，最多检查实际纹理数组的 slice 数。旧历史查询最多尝试一个父级，
无法覆盖一次回溯越过多级的情况。有效范围检查避免用 Clamp 把边缘值拉伸到域外。
中点超出所有流场覆盖时，用原局部速度完成 Euler 回溯；历史位置在所有源级联之外
则返回零，允许泡沫离开模拟区域。

静态旋转流的轨迹例子：角速度 1 rad/s、初始半径 10 m、15 Hz、搬运 2 s，旧回溯
模型使可见泡沫环的半径收缩约 **0.644 m**；中点方法的半径误差低于 **1 mm**。
这个结果不包含纹理双线性重采样。现有平流仍会数值扩散，不是守恒或无耗散方案；
精度结论也不能直接推广到快速随时间变化的流场。

### Filter Waves 只控制白浪波段

白浪检测继续使用 Filter Waves 选择的波段；岸边采样位置和水深改用当前 LOD 的
完整位移。只有过滤级别高于当前 LOD 才补一次采样；过滤为零时复用原样本。
白浪 Jacobian、覆盖阈值、岸边生成公式、默认强度与预热方式保持原样。
调整短波白浪过滤不应再同时改变浅滩泡沫的定位。

### 内置输入以默认 30 Hz 为参考归一化

原 Texture、Vertex Colors、Spline 输入每个子步都输出固定增量；模拟频率从
30 Hz 改为 15 或 200 Hz 时，同一输入每秒添加的量也变化。现在结合已有衰减率 r，
以常量源的积分权重计算每步倍率：

```text
W(dt, r) = (1 - exp(-r * dt)) / r     # r = 0 时取 dt
inputScale = W(actualSubstepDt, r) / W(1/30, r)
```

小指数使用级数避免相近浮点数相减。真实子步为 1/30 s 时分子分母相同，倍率严格
为 1。零步长返回零，原调度也只在正步长时绘制输入；不用预热时长放大输入。
全局量 `_CrestFoamInputScale` 在每个 foam LOD 绘制前绑定，只有上述三个 Shader
显式读取它。负强度保留符号；Override Foam 与未使用此常量的外部自定义输入继续
沿用现有语义。

一致性证明针对固定、未饱和输入及既定衰减率。移动输入在不同渲染帧取样、clamp、
流动和纹理插值仍可能造成逐像素差异；这不是任意条件下完全一致的帧率独立模拟。

## 4. 泡沫着色：覆盖率合成与细节高光

实现：[OceanFoam.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanFoam.hlsl)。

### LOD 先求覆盖率，再混合

两个 LOD 泡沫纹理的黑点阈值和像素羽化分别求值，再混合可见覆盖率。旧顺序先混合
两张不相关纹理再执行非线性阈值，可能让稀疏泡沫在 LOD 中间消失。
两次采样及其导数都保持连续执行；零泡沫仍返回零。保留黑点与羽化定义，在相同
像素足迹、LOD 权重局部恒定的条件下，LOD 端点的数值测试保持一致。实际过渡中
LOD 权重的空间导数会改变旧路径的羽化，因此不保证与旧版逐像素完全相同。

在泡沫量 0.3、两纹理值为 1 与 0、LOD 权重 0.5、Foam Feather=0.4、纹理足迹为零
的平坦纹理测试中，旧覆盖率为
**0**，新覆盖率为 **0.421875**，即端点覆盖率的线性组合。过渡区域可见白沫可能
比旧代码增加，这是覆盖混合顺序的修复；未提高模拟泡沫量或默认覆盖阈值，也没有
声称全局面积或质量严格守恒。

### Flow 两相位按覆盖率混合颜色

两相位的白沫 RGB 先乘各自覆盖率和相位权重，混合后再还原为最终海面合成所需的
直通颜色。中间计算使用 float，分母保护为 `1e-8`，最终 half 输出保持原样。
气泡仍使用原来的加法颜色混合。

例：空相位 `(4, 2, 1, 0)`，有覆盖相位 `(0.2, 0.4, 0.8, 1)`，各占一半，背景
RGB 为 0.1。旧最终颜色为 **(1.10, 0.65, 0.50)**；新结果为
**(0.15, 0.25, 0.45)**。空相位的颜色和高光不再污染有覆盖的一相。

### 太阳高光使用实际泡沫法线

3D 白沫根据包含纹理起伏的 `fN` 求法线方差，再复用现有的高光过滤 helper 与
Specular AA 参数。太阳高光同时乘 `saturate(dot(fN, lightDir))` 和原阴影系数，
消除光在泡沫背面时仍出现正面高光的情况。

[NVIDIA 的 Specular AA 讲义](https://developer.download.nvidia.com/gameworks/events/GDC2016/akaplanyan_specular_aa.pdf)
解释了像素足迹、高光过滤与稳定性／外观偏差的关系。本轮复用仓库的 Phong 过滤
近似，未实现讲义中的完整 NDF 积分，也不把离线高光变宽当作抗闪烁实测结果。

视线在正面掠射、太阳刚位于背面的一组单位向量测试中，设 Specular Falloff=293，
boost、光色和阴影系数均归一为 1，旧高光仍约 **0.985456**，
新结果为零。完整 N·L 权重也会减弱斜入射亮度，即使关闭 Specular AA，这部分
行为仍生效。局部灯继续使用已有水面法线；把泡沫专用法线传给每个局部灯留待后续。

## 5. 最终验证与复现

| 检查 | 本轮结果 | 基线对照／边界 |
| --- | --- | --- |
| DXC + 真实 Unity/URP 头文件 | 197 / 197 通过 | 同一矩阵编译 `8b56660e` 也是 197 / 197；不等同于 Unity 导入或驱动编译 |
| 既有 7 组数值／源码回归 | 63 / 63 通过 | 含 URP、Specular、Foam、Optics、Normal Sampling、Depth Cache、Reflection |
| Wave Normals | 7 / 7 通过 | 基线 6 失败、1 通过 |
| Wave Spectrum / Phase / Bake Contracts | 12 / 12 通过 | 基线 11 失败、1 通过 |
| Foam Dynamics | 13 / 13 通过 | 基线 7 失败、6 通过 |
| Foam Shading | 10 / 10 通过 | 基线 8 失败、2 通过 |
| C# 语法 | 146 文件 × 4 组配置，0 错误 | 不解析 Unity 程序集绑定 |
| 源码结构、JSON、引用与元数据 | 0 错误 | 101 个 Shader 源文件、186 个本地 include 引用 |
| Unity、GPU、Player、设备性能 | 未运行 | 需按第 6 节验收 |

新增 42 项检查在基线中合计 **32 项失败、10 项通过**，当前全部通过。这些脚本读取
真实源码表达式，以独立的几何、解析轨迹和常量源积分作为参考；部分检查执行
IEEE binary16/binary32 存储舍入，其余仍是 CPU double。烘焙格式／资源生命周期
检查是源码契约检查，没有模拟 Unity 的图形资源分配或异常。

最终离线矩阵增加平面／3D 泡沫、Flow 与无细节法线组合，三类 additive 输入及
Override、Gerstner Compute、FFT 初始化／更新和 FFT 烘焙 kernel。它编译选定的
顶点、片元和计算阶段，不是所有 Unity 变体的穷举。使用 DXC v1.9.2609
（`1.9 5512-01b62ad4`）、Unity 6000.6.2f1 的 CGIncludes、URP/Core/UniversalConfig
17.6.0 头文件，Graphics 快照为
`a7e4c051d256a781ab362c64316b125a1e104694`。

当前与基线诊断的类别和数量相同：197 条 `/Gec` 弃用提示、13 条移位表达式字面量类型
提示、54 条向量截断提示、2 条逻辑运算括号提示。这里没有把“编译通过”写成
“零 warning”，也没有在本轮扩大范围修改这些既有诊断。

在仓库根目录运行：

```bash
python tools/check_urp.py
python tools/check_urp_regressions.py
python tools/check_specular_quality.py
python tools/check_foam_quality.py
python tools/check_optical_quality.py
python tools/check_normal_sampling.py
python tools/check_depth_cache_quality.py
python tools/check_reflection_quality.py
python tools/check_wave_normals.py
python tools/check_wave_spectrum.py
python tools/check_foam_dynamics.py
python tools/check_foam_shading.py
dotnet run --project tools/StaticChecks -- crest/Assets
git diff --check

python tools/check_urp_shaders.py \
  --dxc /path/to/dxc \
  --urp-headers /path/to/urp-17.6-headers \
  --unity-includes /path/to/Unity-6000.6.2f1/CGIncludes \
  --output /path/to/check-output
```

四个新增回归脚本均支持 `--ref 8b56660e`，预期出现上表中的旧版失败；离线编译器
也支持该参数，并把旧版完整 Shader/include 快照提取到独立目录，避免混入当前
helper。数字检查不需要检出旧版或改动当前工作区。

## 6. 本地画面与性能验收

先按 [UnityQualityTests](../tools/UnityQualityTests/README.md) 在一次性项目副本中
运行现有检查，再补充以下对照。现有自动 Runner **没有自动覆盖**本节所有项目。

- [ ] **浪面法线**：单组 Gerstner／FFT、整体法线强度 0／1／更高，细节法线开／关；
  观察陡浪的太阳和探针反光、跨 LOD 过渡、动态波和变化水位交界。包含波形接近
  折叠的极端设置，确认退化保护不产生 NaN 或闪屏。
- [ ] **长时间 Gerstner**：分别在 60／600／3600 s 附近观察短波，记录连续帧；
  在真正采用 half 的目标后端复核周期接缝和停顿，不能只用桌面 float 提升路径验收。
- [ ] **FFT 周期与碰撞**：同一海况使用不同 loopPeriod，检查初始振幅；Time Scale
  取 0／0.5／1／1.3，在周期首尾对比画面和查询位移。重新生成旧碰撞烘焙资产，
  检查浮力物体与波面一致、读回格式可用、重复烘焙后临时资源回收。
- [ ] **弯曲 Flow 和级联**：让窄尾迹进入旋转／弯曲水流，使用 15／30／60 Hz，
  移动相机跨越 LOD，测试快速流动超出多个级联及整个模拟范围时的消失行为。
  分别记录轨迹形状和纹理扩散，不把二者混为一个误差。
- [ ] **输入速率**：固定 Texture、Vertex Colors、Spline 输入，先使用未饱和强度，
  对比 15／30／60／200 Hz 的累积与衰减，再检查移动输入和负强度；确认 Override
  及项目自定义输入行为符合原用途。
- [ ] **岸边与 LOD 白沫**：同一浅滩逐步调整 Filter Waves，确认岸边定位不随白浪
  波段改变；缓慢推动相机跨 LOD，观察稀疏白沫覆盖和 Flow 相位切换。
- [ ] **泡沫太阳高光**：3D Foam 开／关、Specular AA 开／关、太阳在地平线两侧，
  对比掠射、阴影和纹理细节。录制相机运动序列，和更高空间采样的参考画面对照，
  同时观察闪烁、过宽高光及斜入射亮度变化。
- [ ] **实际开销与兼容性**：Windows D3D11／D3D12 和 Android Vulkan，记录泡沫
  Compute、海面 Shader、FFT 烘焙的 GPU 时间、寄存器压力、峰值内存及总帧时；
  补跑材质变体裁剪、多相机、反复启停和设备暂停恢复。

| 修改 | 可从代码确认的成本 | 仍需测量的部分 |
| --- | --- | --- |
| 浪面法线 | 样本数不变；坡度与部分采样运算使用 float | 寄存器与实际海面 Shader 时间 |
| Gerstner 波矢／相位 | 部分 half 运算改用 float；模拟纹理和 pass 数不变 | 原生半精度设备的吞吐差异 |
| 中点泡沫回溯 | 非零流速且正步长时通常多一次 Flow 采样；增加有界级联参数查询 | 边界分支、缓存和 Compute 总时间 |
| 岸边定位 | 仅过滤级别高于当前 LOD 时多一次完整位移采样 | 开启过滤时的额外开销 |
| 泡沫着色 | 显式纹理采样数不变；增加覆盖组合与泡沫法线导数运算 | 3D Foam + Flow 组合的片元成本 |
| FFT 烘焙 | 临时颜色 RT 从每像素 16 字节改为 8 字节；最终资产仍为 half | 后端格式支持、读回耗时与实际分配 |

## 7. 仍值得推进的工作

后续[第四轮改进](URP-SURFACE-DETAIL-AND-WAVE-QUERIES.md)已实施下述细节法线重定向
与泡沫局部灯法线传递，并修复烘焙 FFT 查询的一致性和边界。下面保留本轮结束时
的研究范围；第四轮的结果、CPU 成本与验收项目见新报告。

下一步应先完成上述 GPU 对照，把收益和设备成本量化。随后可继续研究：把细节法线
重定向到实际浪面方向；为法线 mip 保存方差统计；将泡沫专用法线传入局部灯；
以更高精度且受限的平流方案降低尾迹扩散。这些工作分别涉及材质外观、资产导入、
灯光接口或额外采样／缓冲，需要独立的视觉和性能依据。

本轮没有新增破波或空气夹带物理，没有实现波谱能量的全新标定、完整微表面泡沫
BRDF、法线方差资产管线或新的浅水求解器。现有波浪／泡沫系统中的具体数值与合成
问题已经修复，后续应在实际画面对照基础上决定更大的模型升级。
