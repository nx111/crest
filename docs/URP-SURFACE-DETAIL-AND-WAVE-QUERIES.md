# URP 真实度改进（四）：细节法线、泡沫局部光与烘焙波浪查询

日期：2026-10-11。分支：`urp`。本轮基线：
[`5700e21b`](https://github.com/nx111/crest/commit/5700e21b93a6ae325d049a5f1ec0bc408a721dc7)。
目标版本保持 **Unity 6000.6.2f1 / URP 17.6.0**。

接续[浪面法线、波浪时序与泡沫稳定性](URP-WAVES-AND-FOAM-STABILITY.md)，本轮已实施
细节法线重定向、泡沫专用法线与方差的局部灯照明，以及烘焙 FFT 查询的坐标、时间
插值和索引边界修复。最终验证为 **225 / 225 个选定离线着色器编译用例、140 / 140
项数值／源码回归通过**；项目内 146 个 C# 文件和质量 Runner 的 1 个 C# 文件，
分别在 4 组条件编译配置下语法检查无错误。

**本轮没有运行 Unity Editor、Burst、GPU 画面对照、Player 或 Android 真机检查。**
历史 D3D11 画面记录属于 `2aaeec77`，不能作为之后四批修改的视觉验收。下面的数值
来自源码表达式与独立数学参考的 CPU 检查；画面改善是依据已定位机制推导的预期。

## 1. 细节法线随浪面方向旋转

实现：[OceanNormalMapping.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanNormalMapping.hlsl)、
[Ocean.shader](../crest/Assets/Crest/Crest/Shaders/Ocean.shader)。

上一轮已恢复宏观浪面的坡度，但细节法线仍直接加到世界 XZ 坡度上。随着大浪变陡，
同一张细节纹理的起伏被压弱；正、反两个方向的细节还会出现不同的偏转角。这会让
细纹在陡浪上变平，使其高光方向和浪面方向不协调。

现在采用从世界向上方向 `+Y` 到宏观浪面法线的最短旋转，将已有的有效细节法线
放到浪面上。依据是原作者的
[Blending in Detail / Reoriented Normal Mapping](https://blog.selfshadow.com/publications/blending-in-detail/)
推导；使用本工程 Y 轴向上的等价形式。输入宏观法线已经单位化，有效细节向量仍
按原材质定义为 `(detail.x, 1, detail.y)`，最后再单位化：

```hlsl
float3 t = macroNormal + float3(0, 1, 0);
float3 u = float3(-detail.x, 1, -detail.y);
float3 finalNormal = normalize(t * dot(t, u) - u * t.y);
```

合成顺序是：先合并波浪和变化水位的坡度，应用 `_NormalsStrengthOverall`，得到
单位宏观法线；再按原采样规则取得细节、应用同一整体强度并旋转。Flow 仍先混合
两个相位的有效细节，然后只旋转一次。完整法线用于计算 Specular AA 方差，之后
才进行既有的水下方向翻转。

以下例子使用 60° 宏观倾角、有效细节长度 0.5、整体强度 1。角度均指细节法线相对
宏观法线的偏转，预期为 `atan(0.5) = 26.565°`：

| 有效细节方向 | 基线偏转 | 修改后偏转 |
| --- | ---: | ---: |
| 顺着宏观倾斜方向 | 5.867° | 26.565° |
| 反着宏观倾斜方向 | 9.065° | 26.565° |
| 与宏观倾斜方向正交 | 14.036° | 26.565° |

平坦宏观水面保留原有两个强度控制的行为；关闭细节或细节为零时，保留宏观坡度的
强度行为；整体强度为零仍得到平面着色法线。采样坐标、次数、显式梯度、纹理解码
和 LOD 混合保持原样。

宏观法线指向上方，因此 `t.y >= 1`，不需要处理旋转到反平行基底的奇点。陡浪上的
合法细节法线可能有负的世界 Y 分量，但仍在宏观法线的外侧半球内；不能再除以它的
Y 或强制翻回世界向上，否则会破坏这次旋转。

这是由宏观法线构造的正交框架，尚未传输完整的水平 choppy 位移／Flow 参数
Jacobian。两个纹理样本的 XY 仍按原定义合成，没有改成完整高度梯度或新的法线
资产格式。陡面细节会比旧版明显，现有 `_NormalsStrength` 可用于重新调节。

## 2. 点光和聚光灯照亮真正的泡沫起伏

实现：[OceanFoam.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanFoam.hlsl)、
[OceanAdditionalLights.hlsl](../crest/Assets/Crest/Crest/Shaders/OceanAdditionalLights.hlsl)
以及主 Shader 的数据传递。

3D 泡沫的太阳照明已经使用包含泡沫纹理起伏的 `fN`，局部光源却仍使用普通水面
法线和水面方差。同一块泡沫在太阳和船灯下会呈现不同的起伏方向，高光过滤也没有
对应泡沫本身的变化。

现在 `ComputeFoam` 导出已经求得的泡沫法线与方差。每盏额外灯用这组数据计算
泡沫漫反射和过滤后的 Phong 高光。水体散射和水面高光继续使用水面自己的法线。
平面泡沫沿用原有照明分支；关闭泡沫时不参与泡沫光照。

数值例：水面法线竖直，泡沫法线向侧面倾斜 45°，水平侧光与该侧面同向；只计算
漫反射，颜色、强度及衰减均归一为 1。旧局部泡沫项为 **0**，新值为
**0.70710678**，与该泡沫方向的太阳漫反射项一致。这只比较一项光照表达式，不是
整张画面的亮度或真实性评分。

### Flow 保留两组法线，分别照明后再合成

两个 Flow 相位保留各自的法线和方差；其局部灯权重由相位权重 `w` 和覆盖率
`alpha` 共同确定：

```text
coverage = w0 * alpha0 + w1 * alpha1
q0 = w0 * alpha0 / max(coverage, 1e-8)
q1 = w1 * alpha1 / max(coverage, 1e-8)
foamLight = q0 * Light(normal0, variance0) + q1 * Light(normal1, variance1)
```

这与上一轮 `CrestBlendWhiteFoam` 的覆盖颜色合成一致。乘积、覆盖总和与权重使用
float，避免先把覆盖总和舍入到 half；最终白沫 alpha 仍只在海面合成处乘一次。
零覆盖相位不会给另一相提供高光、法线或方差。极小覆盖处使用与颜色合成相同的
`1e-8` 分母保护，并不宣称在那里仍有严格的单位权重和。

必须分别计算两个相位的光照，因为高光是法线的非线性函数。例如两相位法线分别
倾斜 ±30°，视线和灯光都向上，指数 293、关闭 AA、只计算归一化高光，旧水面
法线会产生 **1** 的峰值；先平均两个泡沫法线也会产生同一峰值。逐相位参考值
接近零。CPU double 公式约为 `5.44e-89`，这个数低于 float 存储范围，不表示 GPU
会保留该精度。

所有泡沫纹理采样、法线和导数仍在灯光循环及覆盖分支之前完成。URP 每次循环
迭代仍只获取一次 Light，两相位共用距离、锥体、阴影、cookie 与光照层处理。
第二相位的光照仅在 `_FLOW_ON` 中编译；无覆盖相位可以跳过其照明算术。

本轮没有调整泡沫生成浓度、默认值或贴图差分步长。现有 3D 泡沫差分步长仍与相机
深度相关，该标定问题需要专门的近远距离画面对照，不能通过本次传递法线自动解决。

## 3. 烘焙 FFT 查询使用一致的水面位置

实现：[FFTBakedData.cs](../crest/Assets/Crest/Crest/Scripts/Shapes/FFT/FFTBakedData.cs)、
[CollProviderBakedFFT.cs](../crest/Assets/Crest/Crest/Scripts/Collision/CollProviderBakedFFT.cs)。

### 位移 Query 遵守世界 XZ 的接口契约

[ICollProvider](../crest/Assets/Crest/Crest/Scripts/Collision/CollProvider.cs) 要求返回的位移
属于“会移动到所查询世界 XZ 的水面点”；世界高度等于 Sea Level 加上位移 Y。
旧烘焙 provider 直接采样未位移位置，与这个契约不一致。旧高度采样却已经进行
逆位移搜索，因此同一点的两个查询可能给出不同高度。

现在两者都复用 `SampleDisplacementXZTWithInvert`。它从查询位置出发，用原有
**3 次固定点迭代**寻找未位移位置，再返回完整位移。底层高度采样直接取同一结果
的 Y；provider 的世界高度仍按原调用链加上 Sea Level。

真实 half 数组测试使用 16 m 周期、水平／垂直幅度各 1 m、128 texel 的波浪，在
`x = 4 m` 处，基线 provider 位移 Y 为 **0**，底层高度为 **−0.358920 m**，相差约
**35.9 cm**。修改后两种入口使用同一个反演结果，消除了这类 API 内部不一致。
这个数是构造波场中的反例，不代表任意船体原先都有相同误差。

原公共 `SampleDisplacementXZT` 保留“输入为未位移坐标”的 raw API 语义；已有确实
需要原始位移场的调用可以继续使用它。没有变更烘焙资产布局、half 四通道格式或
既有公共采样方法签名。

### 先插值位移场，再反演

旧高度采样分别对两个烘焙帧反演，然后线性插值两个高度。但对有水平位移的波浪，
反演与时间插值不能交换顺序。

新实现先确定两个帧索引和时间权重，在每一步迭代中采样该时刻的插值位移场。
索引和权重只计算一次，供全部迭代复用。例如两个完全相反的帧在权重 0.5 时，
插值后的位移场为零；在上面的波场例子中，旧高度路径仍输出约 **−0.358920 m**，
新路径恢复为 **0**。

### 时间和空间端点都做最终整数回绕

旧负时间公式 `1 - abs(t / period) % 1` 在负整周期得到 1。周期 32 s、128 帧时，
`t = -32 s` 或 `-64 s` 会产生非法索引 128；极小负时间也可能因浮点舍入到达该
端点。速度查询本来就会取 `t - 0.06 s`，所以起始时刻附近也可能进入负时间路径。

新代码使用 `frac(t / period)`，并在乘帧数后对整数帧索引再次取模。插值权重从
**取模前**的浮点帧坐标和其 floor 求差，避免把取模后的零索引误当成权重基底。
这同时处理了 binary32 中 `frac` 在极小负数处舍入为 1 的情况。

空间双线性采样也增加 U0／V0 的整数回绕。在 8 m 域、64 texel 中，第一个 texel
中心是 `0.0625 m`；它的前一个 binary32 数值约为 `0.0624999963 m`。旧代码把
微小负 texel 坐标加 64 后可能舍入成 64，导致跨行、跨 LOD 或越界。现在首个和
相邻 texel 索引都在合法范围内，alpha 的计算和原有 UV 约定保持原样。

上述范围针对有效的烘焙参数与有限坐标／时间，并不是损坏资产或 NaN 输入的验证器。
另外将 `using UnityEditor` 放进 `UNITY_EDITOR` 条件，与文件底部 Inspector 的
条件一致，避免 Editor 命名空间进入 Player 源码；完整 Player 程序集绑定仍待 Unity
构建确认。

## 4. 成本、兼容性和准确性范围

| 修改 | 可从源码确认的成本 | 需要测量或复核的部分 |
| --- | --- | --- |
| 细节法线重定向 | 额外的宏观归一化和旋转算术；纹理采样数不增加 | 陡浪材质外观、片元时间和寄存器 |
| 3D 泡沫局部灯 | 不增加纹理或 Light 获取；Flow 每灯最多计算两次泡沫照明，并保留两组法线／方差 | 多盏船灯与 Android 寄存器、分支和片元开销 |
| 底层 `SampleHeightXZT` | 逐帧空间位移场采样调用仍为 8 次 | Burst 对未使用分量的优化和完整作业耗时 |
| provider 位移 Query | 逐帧空间位移场采样调用由 2 次增至 8 次 | 实际查询点数、物体数量与 CPU 帧时 |
| raw `SampleDisplacementXZT` | 仍为 2 次逐帧空间位移场采样调用 | 使用者须确实提供未位移坐标 |

这些采样计数指 `SampleDisplacementFromXZ` 的调用；每次仍按原逻辑遍历烘焙 LOD
并做双线性采样。它们不是 GPU 纹理读取数，也不是整项 CPU 查询耗时的倍数。
**常用 [SampleHeightHelper](../crest/Assets/Crest/Crest/Scripts/Collision/SamplingHelpers.cs)
的高度便捷方法实际走 provider 位移 Query，也会受 2 → 8 的 CPU 成本变化影响。**
不能把“底层 Height 仍为 8 次”概括成所有高度查询成本不变。

CPU 继续使用 3 次迭代，当前 GPU Query 使用 4 次。强烈水平位移或折叠不保证固定
点迭代收敛；有限迭代、half 数据和时间／空间插值仍有误差。此次统一的是同一个
烘焙场内的接口语义，未补齐 GPU 动态波、局部水位或全部 LOD 过滤行为，也不宣称
CPU 与 GPU 逐位一致。

本轮没有新增材质开关、序列化字段、GPU pass、缓冲或法线资产格式。已有材质自动
使用对应关键字下的修正。只从本轮基线升级，不要求变换已有烘焙资产格式；若尚未
执行上一轮要求的 FFT 重新烘焙，仍须先完成，以匹配修正后的波谱与时间缩放。

## 5. 最终验证与复现

| 检查 | 当前结果 | 基线对照／验证范围 |
| --- | --- | --- |
| DXC + 真实 Unity/URP 头文件 | 225 / 225 通过 | 选定阶段与变体，不等于 Unity 导入或设备编译 |
| 既有 11 组数值／源码回归 | 105 / 105 通过 | 保留波浪时间域等既有不变量 |
| Detail Normals | 10 / 10 通过 | `5700e21b`：6 失败、4 通过 |
| Foam Lighting | 12 / 12 通过 | `5700e21b`：10 失败、2 通过 |
| FFT Queries | 13 / 13 通过 | `5700e21b`：10 失败、3 通过 |
| C# 语法 | 项目 146 文件、Runner 1 文件，各 × 4 配置，0 错误 | 不解析 Unity 程序集或 Burst 编译 |
| 源码结构、JSON、引用与元数据 | 0 错误 | 101 个 Shader 源文件、186 个本地 include 引用 |
| Unity、GPU、Player、设备性能 | 未运行 | 需按第 6 节验收 |

新增 35 项检查在基线中合计 **26 项失败、9 项通过**，当前全部通过。它们读取真实
源码表达式或检查跨文件数据传递；数值参考包括独立轴角旋转、按覆盖区域计算的
光照、周期双线性 half 数组采样和解析矩阵反演。FFT 解释器按一条 SIMD lane 执行
binary32 运算和真实的四通道 half 布局；细节法线测试包含选定的 binary32 运算
用例，泡沫测试包含 half／float 存储舍入，其余数值使用 CPU double。都不模拟
完整 GPU half／FMA 或 Burst 指令行为。

离线编译矩阵增加 7 个局部照明组合，覆盖无泡沫、平面泡沫、3D 泡沫、Flow、无
细节法线、per-vertex 资产模式和 Forward+。这是扩大检查覆盖，未新增对应的生产
关键字或运行时变体。使用 DXC v1.9.2609（`1.9 5512-01b62ad4`）、Unity 6000.6.2f1
的 CGIncludes、URP/Core/UniversalConfig 17.6.0，Graphics 快照为
`a7e4c051d256a781ab362c64316b125a1e104694`。编译通过不等于没有 warning。

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
python tools/check_detail_normals.py
python tools/check_foam_lighting.py
python tools/check_fft_queries.py
dotnet run --project tools/StaticChecks -- crest/Assets
dotnet run --project tools/StaticChecks -- tools/UnityQualityTests
git diff --check

python tools/check_urp_shaders.py \
  --dxc /path/to/dxc \
  --urp-headers /path/to/urp-17.6-headers \
  --unity-includes /path/to/Unity-6000.6.2f1/CGIncludes \
  --output /path/to/check-output
```

三个新增脚本支持 `--ref 5700e21b`，可不切换工作区重现上表的基线失败。既有
`check_wave_spectrum.py` 只适配共享时间 helper 的调用路径，未改变原有时间域
不变量或容差。

### 原 Built-in 对照只比较共同的平面法线照明

[QualityRunner](../tools/UnityQualityTests/README.md) 的可选原版对照现在在两条路径
中同时设置 `_NormalsStrengthOverall = 0`，继续关闭原版没有的 Specular AA。
实际波浪几何仍相同；比较的是固定单太阳、固定环境的共同光照表达式。整体强度、
方差、Shader 和关键字在 `finally` 中恢复。

原平均 RGB 差异阈值 **0.015 保持不变**。这样不会把有意修正的浪面／细节法线
差异混进旧版光照等价检查。此对照不验证 RNM 画质，也不证明新法线与旧版外观
一致。本轮只修改并语法检查 Runner，没有执行新的截图对照。

## 6. 本地画面与性能验收

先按 [UnityQualityTests](../tools/UnityQualityTests/README.md) 在一次性项目副本中
运行现有检查，再补以下项目；Runner 没有自动完成这些专用场景。

- [ ] **细节方向和材质强度**：平水、30°／60° 陡浪、变化水位；细节开／关，两个
  强度分别取零和常用值。让太阳和有明显方向的反射环境扫过波面，观察细纹是否
  顺着浪面，评估陡面增强后的外观。
- [ ] **Flow、LOD 和水下**：移动相机跨 LOD；经过 Flow 相位重置；缓慢穿越水面。
  检查完整法线、反射和 Specular AA 的连续性，包含接近折叠的极端波形。
- [ ] **泡沫船灯**：关闭太阳高光，用点光与聚光扫过 3D 白沫；比较主光与局部光
  的起伏方向，开／关阴影、cookie、光照层及 Specular AA。检查平面泡沫和无泡沫。
- [ ] **双相位泡沫**：制造一相有覆盖、另一相为空或法线方向不同的 Flow 场景，
  观察局部高光是否凭空出现，检查相位重置、微小覆盖和 LOD 过渡。
- [ ] **FFT 查询与船体**：使用已按上一轮要求重烘焙的资产，开启水平 chop；在同一
  世界点比较位移 Y 加海平面与高度 Query，扫过两个烘焙帧的中间时刻和循环接缝。
  验证船体贴水、法线／速度结果，以及起始时刻的负历史时间和空间接缝。
- [ ] **Windows／Android 成本**：分别测量海面片元和烘焙查询作业；逐步增加船灯、
  物体和查询点数。检查 D3D11／D3D12 与 Android Vulkan 的总帧时、寄存器和缓存
  行为，包含 `SampleHeightHelper` 路径、Forward+ 及 Player 变体裁剪。

## 7. 后续值得单独推进的工作

本轮优先修复可由表达式和接口直接确认的问题。更大的模型改动仍应由画面对照和
设备成本决定：法线 mip 方差统计需要资产导入流程；泡沫差分的距离标定需要重新
评估材质强度；减少尾迹扩散的受限高阶平流会增加采样或缓冲；强烈 choppy 波的
更稳健求逆需要明确收敛标准和 CPU 预算。

这些工作没有混入本次修改。本轮也没有新增破波、空气夹带或完整微表面泡沫 BRDF。
当前交付修正了细节方向、局部泡沫光照和烘焙水面查询的一致性；真实画面收益与
Windows／Android 开销仍需上述本地验收确认。
