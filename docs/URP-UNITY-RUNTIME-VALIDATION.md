# Unity 实机复核：真实度修改

日期：2026-10-11。被测提交：`90a93293d70f73df224579c6373e12c70d55a697`，分支 `urp`。
根据《研究并优化渲染真人感》会话复核此前四轮修改。

测试使用 Unity **6000.6.2f1 / URP 17.6.0 / Windows / NVIDIA GeForce GTX 1650**，
CPU 为 AMD Ryzen 7 4800H。通过 Unity 实际渲染、Compute、NativeArray 和 Burst
执行检查，未使用 Shader 或 Unity API 桩。

一次性工程在 `D:\Games\work\crest-validation-20261011-90a93293`。原项目未打开；
验证副本中的 **213 个 Crest 生产 `.cs/.shader/.hlsl/.compute` 文件与源码 SHA256
一致**。本轮仓库修改是测试工具与记录，未改变生产渲染实现。
Windows 与 Android 两个验证副本的比对记录见
[生产代码 SHA256 清单](_media/urp-realism/2026-10-11/production-sha256.txt)。

## 编辑器结果

最终 D3D11 扩展运行退出码 **0**，日志有 `CREST_QUALITY_RESULT PASS`；
**322 项断言通过，55 张 960 × 540 截图，0 条 Search 索引异常记录**。
每次 HDR 读回检查 RGBA 有限值；检查实际编译错误和错误 Shader 的洋红像素。

| 修改或路径 | 实机检查与结果 | 结论边界 |
| --- | --- | --- |
| 基础海面与灯光 | 点光、聚光、距离、光锥、阴影、逐顶点资产模式及基础 Forward+ 灯循环通过 | 不代表所有光照层、cookie、Deferred 或相机堆叠组合通过 |
| Specular AA | 关闭／开启／恢复截图通过；线性 RGB 平均差值 `0.0045160522` | 验证参数生效；没有运动序列或超采样参考，不能宣称闪烁降低多少 |
| RNM 细节方向 | 实际生产 HLSL 在宏观法线 0°／30°／60°、正负与零细节输入下，单位长度和角度检查全部通过 | GPU 函数检查不是所有极端浪形的场景验收 |
| RNM 海面截图 | 排除遮挡海面的岛屿，对比 `5700e21b`，同几何／时间／材质的平均 RGB 差值 `1.85854825e-6` | 差异很小，只确认这个视角下路径有变化，不据此证明画质提高 |
| 泡沫局部灯 | 45° 泡沫法线受水平侧光的漫反射值 `0.7071068`；空相位无贡献；逐相位合成通过；专用方差使测试高光 `2 → 1.08175` | 没有完整运动船灯或 LOD 相位连续性验收 |
| 原 Built-in 光照对照 | 固定单太阳、共同平面法线条件下，平均 RGB 差值 **0**，保留原阈值 `0.015` | 只比较共同光照，不比较有意改变的正常浪面／细节法线 |
| FFT 世界高度与位移 | 99 组 SIMD 坐标／时间边界用例高度与求逆位移 Y 差值 **0**；含负时间、纹素中心两侧、周期和大坐标 | 使用合成半精度资产，未重烘焙所有用户资产或验证任意强 chop 收敛 |
| 水平 chop 修正 | 实际 Unity half 数组中原始 Y 为 **0**，世界点高度为 **−0.3589198**，与预期一致 | 确认世界点求逆作用，而非把原始参数空间高度当世界高度 |
| FFT Burst 与异步提供器 | 8192 点 Burst 与 managed 生产采样最大差值 **0**；真实碰撞提供器在 −16、−1e−9、0、8、16 秒返回一致高度、位移、单位法线及有限速度 | 不代表整个船队浮力或帧预算 |
| FFT Compute 与烘焙 | 实际生成和半精度读回通过；16 秒循环最大差值 `0.0001220703`；改变循环长度初始场差值 **0**；Time Scale 0／0.5／1／1.3 循环闭合通过 | 小分辨率诊断，不是完整烘焙资源峰值或 GPU 性能比较 |
| 平面反射 | 真实 mip 链、128／512 尺寸与 HDR／mip 重建、释放 GPU 资源后恢复、禁用再启用恢复通过 | 不包含设备暂停恢复或全部粗糙度视觉标定 |
| 折射与水下 | 普通透明物体海面前合成、法线扰动、两种水下合成时机、MSAA 1／2／4／8、0.75 渲染缩放、正交烟雾检查通过 | 未覆盖高海拔深度缓存 EXR 往返、体积边缘及 XR |
| 示例场景 | main、boat、BoatWakes、threeboats、Examples、LakesAndRivers、whirlpool 均完成实际渲染 | 示例素材本身包含简化地形和模型，不能据此认定整体场景达到照片真实度 |

CPU 合成 FFT 作业包含 8192 点的调度和等待，20 次采样：

| 路径 | 中位数 | p95 |
| --- | ---: | ---: |
| 原始位移采样 | 0.0812 ms | 0.1315 ms |
| 世界点求逆采样 | 0.2527 ms | 0.3234 ms |

这些是该诊断的绝对时间，不是相对旧提交的成本变化，也不是整帧或船队基准。

## 发现：半精度泡沫的频率误差

以初始泡沫 1、衰减率 0.8、无源项、无流动运行实际 `UpdateFoam` 两秒，
理论结果为 `exp(-1.6) ≈ 0.2018965`。RFloat 最大相对误差约 **0.000539%**，
但 RHalf 的逐步存储会积累可见误差：

| 模拟频率 | RHalf 结果 | 相对理论结果的偏差 |
| --- | ---: | ---: |
| 15 Hz | 0.2000732 | −0.903% |
| 30 Hz | 0.1981201 | −1.870% |
| 60 Hz | 0.1939697 | −3.926% |
| 200 Hz | 0.1762695 | −12.693% |

默认 `SimSettingsFoam` 使用 `R16_SFloat`，因此不能把 float 表达式的解析解等同于
默认模拟纹理在所有频率下完全一致。诊断对 RHalf 只断言有限值，单独记录误差，
没有放宽 RFloat 的精度阈值。高频下要求严格寿命一致时，应另行评估现有设置中的
`R32_SFloat` 及其内存、GPU 成本；本次未修改默认格式。

## 反复出现的错误及修复

原完整栈为 `ArgumentOutOfRangeException → SearchDatabase.EnumerateAll →
GetDefaultSearchDatabase → SearchInit.IndexationOnStartup`。
它在 Unity 编辑器启动搜索索引时出现，栈中没有 Crest 渲染调用。

触发条件是测试在首次创建默认索引之前立即进入 Play Mode。
结合 [Unity 官方 SearchDatabase 参考源码](https://github.com/Unity-Technologies/UnityCsReference/blob/master/Modules/QuickSearch/Editor/Indexing/SearchDatabase.cs)
推断：Play Mode 阻止首次创建默认索引，随后枚举代码仍访问数据库列表第一个元素。
官方参考源码的版本可能不同于本机编辑器；实际修复由以下本机复测确认。

`QualityRunner.Start` 现在在 Edit Mode 调用公开的
`UnityEditor.Search.SearchService.IsIndexReady(null)`，初始化默认数据库后再进入
Play Mode。索引内容可异步构建，无需等待全部索引。未关闭搜索、删除 Library、
修改全局首选项或修改生产海面代码。

将验证工程的 `UserSettings/Search.index` 移到备份名称后重新运行，日志按顺序记录：

```text
Crest quality: search database initialized before Play Mode; index ready=False
Start Indexing on Editor startup
CREST_QUALITY_RESULT PASS
```

最终日志没有该越界异常，Search.index 重新创建。日志监听提前到初始化阶段；
Search 错误现在既有诊断标记，也会让测试失败，**不再从错误断言中排除**。
初次扩展诊断另遇到过 Mono 的 `InsufficientExecutionStackException`；在测试
managed 调用外加禁止内联包装后复测通过，生产 FFT 与 Burst 调用保持原样。

## Windows Player

Development Mono Windows x64 构建完成，退出码 **0**，BuildReport 为
**Succeeded，0 errors，7 warnings**，产物总量 326,956,108 字节。Windows 和
Android 构建按顺序执行；两个 Windows API 的 Player 也依次运行。
构建摘要见 [Windows BuildReport](_media/urp-realism/2026-10-11/windows-build-result.txt)。

在 GTX 1650 上，以**可见、未最小化的 960 × 540 窗口**运行：

| API | 运行退出码 | 断言 | 渲染截图 | 实际图形设备 |
| --- | ---: | ---: | ---: | --- |
| D3D11 | 0 | 60 PASS | 9 | Direct3D 11.0，feature level 11.1 |
| D3D12 | 0 | 60 PASS | 9 | Direct3D 12，feature level 12.1 |

七个示例场景、两种水下合成时机、12 组 RNM 和 5 组泡沫灯光数值检查均完成，
生产 HLSL Shader 在经过 Player stripping 后实际执行。RGBA 有限值、错误 Shader
像素和数值阈值通过；正常窗口运行未记录 Crest 运行时、Shader 或 RenderGraph 错误。
Windows 泡沫侧光为 `0.7071068`，方差测试高光为 `2 → 1.08175`。
水下截图同样很暗，不能据此证明水下外观已完成验收。

| 场景 | D3D11 帧间隔中位数／p95 | D3D11 GPU 中位数 | D3D12 帧间隔中位数／p95 | D3D12 GPU 中位数 |
| --- | ---: | ---: | ---: | ---: |
| main | 16.67／16.74 ms | 4.27 ms | 16.67／16.67 ms | 1.93 ms |
| boat | 16.67／16.87 ms | 5.79 ms | 16.67／16.76 ms | 2.27 ms |
| BoatWakes | 16.67／16.88 ms | 5.90 ms | 16.67／16.98 ms | 3.06 ms |
| threeboats | 16.67／16.69 ms | 5.23 ms | 16.67／16.93 ms | 2.08 ms |
| Examples | 16.67／16.68 ms | 4.43 ms | 16.67／16.69 ms | 2.33 ms |
| LakesAndRivers | 16.67／16.97 ms | 6.52 ms | 16.67／16.77 ms | 2.21 ms |
| whirlpool | 16.67／16.81 ms | 4.41 ms | 16.67／16.97 ms | 2.05 ms |

上述是各场景 60 帧的短样本，目标上限 60 FPS，不能从这个表推导修改前后的性能
变化、发布配置预算或长期稳定性。Android 的屏幕分辨率与此不同，不能直接对比。

**首次隐藏窗口运行失败，不能删去。** D3D11 第一次以隐藏窗口启动时，两次记录
`Crest: Query ring buffer exhausted`，运行退出码 **1**；虽然 59 项图像／数值断言
通过，最终结果仍为 FAIL。当时 GPU 帧时不可用，其帧间隔不作为正常渲染性能证据。
调用栈指向 `QueryBase.SegmentRegistrarRingBuffer.AcquireNew → UpdateQueries →
OceanRenderer.RunUpdate`。该生产文件最后修改于 `3eb56dc0`，不在此前四轮修改中。

随后可见窗口 D3D11 和 D3D12 均通过；再次隐藏运行也为 60 PASS、退出码 0，但
GPU 帧时仍不可用。因此尚未证明错误只由隐藏窗口引起。首次启动时异步 GPU
读回积压是排查线索；源码注释也提示不可见视图可能使读回停止处理。没有增加
环缓冲大小、忽略错误或改动查询实现来制造 PASS。首次启动／读回延迟触发条件
仍需专门复现，这里不宣称此问题已修复。

记录见 [首次失败断言](_media/urp-realism/2026-10-11/windows-d3d11-initial-failure-results.txt)、
[首次失败完整日志](_media/urp-realism/2026-10-11/windows-d3d11-initial-failure-player.log)、
[隐藏窗口复跑断言](_media/urp-realism/2026-10-11/windows-d3d11-hidden-repeat-results.txt) 和
[隐藏窗口复跑日志](_media/urp-realism/2026-10-11/windows-d3d11-hidden-repeat-player.log)。

D3D12 的完整启动日志还包含
`failed to query info queue interface (0x80004002)`，随后确实创建 D3D12 设备并
完成全部渲染／Compute 检查。[Microsoft 文档](https://learn.microsoft.com/en-us/windows/win32/api/d3d12sdklayers/nn-d3d12sdklayers-id3d12infoqueue)
说明 InfoQueue 用于调试消息，查询它要求启用调试层；本次未专门配置或验收 D3D12
调试层，也未改动系统图形工具或驱动。正常渲染 PASS 不代表这项诊断接口可用。
构建报告还含向量截断、可能未初始化背景变量等警告；构建编辑器关闭时另有
两条 JobTempAlloc 残留提示，没有 Crest 调用栈，本轮未作泄漏归因或内存峰值验收。

有效结果见 [D3D11 断言及帧时](_media/urp-realism/2026-10-11/windows-d3d11-results.txt)、
[D3D11 完整日志](_media/urp-realism/2026-10-11/windows-d3d11-player.log)、
[D3D12 断言及帧时](_media/urp-realism/2026-10-11/windows-d3d12-results.txt) 和
[D3D12 完整日志](_media/urp-realism/2026-10-11/windows-d3d12-player.log)。
海面截图：[D3D11](_media/urp-realism/2026-10-11/windows-d3d11-main.png)、
[D3D12](_media/urp-realism/2026-10-11/windows-d3d12-main.png)。
完整截图保留于一次性工程的 `PlayerCaptures-d3d11-visible` 和
`PlayerCaptures-d3d12-visible`。

## Android Player

三星 **SM-G9750 / Android 12（API 31）/ Adreno 640** 上完成 Development ARM64
IL2CPP APK 实测。应用实际使用 **Vulkan 1.1.0**，驱动支持 1.1.128，驱动版本
512.502.0（2021-10-01）。屏幕渲染为 **2173 × 1017**；诊断截图为 960 × 540。
APK 为 204,349,319 字节，构建报告为 **Succeeded，0 errors，9 warnings**。
构建摘要见 [Android BuildReport](_media/urp-realism/2026-10-11/android-build-result.txt)；
其中 size 是构建报告的产物总量，不是 APK 文件大小。

脚本最终 **60 项断言通过，9 张渲染截图，`CREST_PLAYER_AUDIT PASS`**：七个示例
场景均有海洋模拟／瓦片、相机及实际图像，RGBA 有限、无错误 Shader 像素；两种
水下合成时机通过有限值／错误像素检查。12 组生产 RNM 的长度／角度检查通过；
泡沫侧光值为 `0.7070313`，专用方差使测试高光 `2 → 1.082031`，相位合成通过。
这些使用与编辑器相同的数值阈值，没有为移动端放宽阈值。
水下相机位于示例岛屿附近，截图很暗；这里只验证了有限值和错误像素，
不能据此认定水下外观、光照或体积边缘已完成视觉验收。

| 场景 | 帧间隔中位数 | p95 | GPU 中位数 |
| --- | ---: | ---: | ---: |
| main | 50.11 ms | 66.84 ms | 53.02 ms |
| boat | 83.53 ms | 100.24 ms | 78.80 ms |
| BoatWakes | 100.31 ms | 116.93 ms | 107.93 ms |
| threeboats | 83.52 ms | 100.24 ms | 86.26 ms |
| Examples | 50.10 ms | 66.81 ms | 52.32 ms |
| LakesAndRivers | 200.42 ms | 200.50 ms | 193.96 ms |
| whirlpool | 83.52 ms | 116.91 ms | 89.70 ms |

这组默认画质的实际帧率约 **5–20 FPS**，未达到 30／60 FPS 预算。每场景只采样
60 帧、目标上限 60 FPS，设备测试后电池温度为 33.8°C；这些是 Development 配置
的绝对烟雾样本，没有旧提交对照，不能将其归因于本轮真实度修改。移动端还需要
针对模拟分辨率／LOD、反射、地形和屏幕分辨率做独立性能调优。

**完整启动日志并非零错误。** 在托管测试启动之前，原生 Unity 日志记录了一条
`java.lang.ClassNotFoundException: com.google.android.play.core.assetpacks.AssetPackManager`。
原始错误行另存于 [启动错误记录](_media/urp-realism/2026-10-11/android-vulkan-full-log-errors.txt)。
随后图形初始化、所有场景和诊断正常完成；日志中未见 Shader 错误或原生崩溃。
这是启动时资源包依赖问题的线索，不能由脚本最终 PASS 将它排除。源项目没有调用
AndroidAssetPacks；[Unity 6.6 官方 API 文档](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Android.AndroidAssetPacks.html)
说明该接口依赖 PlayCore。这里只确认类缺失和发生时机，尚未确认引擎探测该类的原因，
未通过加入未使用的 Google 库或忽略错误来改变测试结果。

同一 APK 再次实机运行时，渲染部分仍为 **60 PASS／9 张图**，同一启动类缺失错误
再次出现。更新后的主机工具已实际复测：完整日志错误数为 **1**，CLI 退出码 **1**，
即使托管渲染结果为 PASS，整次测试也不会被报告为无错误成功。记录见
[复测断言](_media/urp-realism/2026-10-11/android-vulkan-repeat-results.txt)、
[复测完整日志](_media/urp-realism/2026-10-11/android-vulkan-repeat-logcat.txt) 和
[复测启动错误](_media/urp-realism/2026-10-11/android-vulkan-repeat-full-log-errors.txt)。

构建还记录原项目 Active Input Handling 为 Both 的 Android 不支持警告，以及
向量截断、可能未初始化背景变量等 Shader 警告。源项目配置未为本轮测量修改。
`-logFile` 未在设备生成文件，完整日志改由该应用 PID 的 logcat 获取。

原始设备产物位于 `D:\Games\work\crest-validation-android-20261011-90a93293\AndroidCaptures-vulkan-20261011-185054`；仓库保存了
[断言及帧时](_media/urp-realism/2026-10-11/android-vulkan-results.txt)、
[完整 Unity 启动／运行日志](_media/urp-realism/2026-10-11/android-vulkan-logcat.txt)、
[海面截图](_media/urp-realism/2026-10-11/android-vulkan-main.png) 与
[水下截图](_media/urp-realism/2026-10-11/android-vulkan-underwater.png)。

## 产物与复现

工具说明：[UnityQualityTests](../tools/UnityQualityTests/README.md)。
以下路径均在上述一次性工程下：

- `realism-d3d11-complete.log`：最终编辑器完整日志。
- `QualityCaptures/results.txt`：322 项断言、测量与说明。
- `QualityCaptures/*.png`：55 张 gamma 预览；断言使用线性 HDR 读回。
- `QualityCaptures-basic-d3d11`：本轮最初基本检查，195 项通过、40 张图；其中仍有
  修复前的 Search 索引异常记录，不能作为最终“无索引异常”的证据。

仓库中保存了本轮的 [最终编辑器结果](_media/urp-realism/2026-10-11/editor-results.txt)、
[日光海面](_media/urp-realism/2026-10-11/editor-daylight.png)、
[RNM 对照中的当前画面](_media/urp-realism/2026-10-11/editor-rnm-current.png) 与
[Flow 泡沫画面](_media/urp-realism/2026-10-11/editor-foam-flow.png)。
完整原始日志和其余截图保留在一次性工程中。

复现时复制源码 Assets、Packages、ProjectSettings，安装可选诊断和参考 Shader，
运行 `QualityRunner.Start -crestSpecularAA -crestRealismAudit -force-d3d11`。
不要加入 `-nographics` 或 `-quit`；编辑器诊断需要 Play Mode 并自行退出。

## 尚未覆盖的验收

长时间原生 half Gerstner、弯曲 Flow 与跨级联传输、移动泡沫输入／LOD、相机运动高光与超采样
参考、高海拔深度缓存 EXR 往返、完整体积／相机堆叠／XR、生产发布配置、变化
前后的 GPU 成本和内存峰值仍需专门场景或目标设备。这里的 PASS 限于已执行的
检查，不替代此前各轮报告中的完整视觉与性能清单。
