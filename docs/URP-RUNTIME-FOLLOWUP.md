# URP 实机回归跟进：查询回读、泡沫精度与移动端预算

日期：2026-10-11。目标版本：**Unity 6000.6.2f1 / URP 17.6.0**。

后续实测及本地修复见 [9df1db9f Unity / Player 复测](URP-UNITY-RUNTIME-RETEST-9df1db9f.md)：原生查询积压与运行期间清理通过，但发现立即退出时的 GPU buffer 回收缺口，以及 Unity 6000.6.2f1 的 Android AssetPackManager 启动依赖错误；两项已实施修复并进行跨平台复验。下面保留原始基线分析。
本轮代码基线及用户实测提交为 [`90a93293`](https://github.com/nx111/crest/commit/90a93293d70f73df224579c6373e12c70d55a697)。

实机结果支持保留前四轮的法线、泡沫光照及烘焙波浪查询修正，也暴露了查询回读积压、半精度泡沫寿命偏差和移动端帧时问题。
本轮实施查询资源生命周期修复、验证工具启动错误收集修正，以及泡沫存储精度诊断；移动画质按明确的成本假设安排 A/B，尚未改变示例的默认画质。
**用户上传的 GPU 结果属于 `90a93293`；本轮新增代码只完成下面列明的离线检查，仍需实机复测。**

## 1. 实机证据及其边界

证据来自用户提供的 `URP-UNITY-RUNTIME-VALIDATION.md` 与 `2026-10-11.zip`。
压缩包包含 28 个结果、日志及图片文件，其中 `production-sha256.txt` 的 **213 个生产文件**已逐一与 `git show 90a93293:<path>` 的原始字节核对，全部匹配。
本报告汇总这些证据；上传的原始日志、设备路径及截图不随本次代码提交发布。
附件没有提供扩展的 Editor/Player Runner 源码、Gradle 导出工程或完整的构建编译警告，因此不宣称已审查其全部测量实现。

| 实测环境 | 已记录结果 | 不能从结果推出的结论 |
| --- | --- | --- |
| Windows Editor，GTX 1650，D3D11 | 322 项断言、55 张图；最终运行没有 Search 索引异常 | 全部材质组合、时间稳定性和视觉真实度均已通过 |
| Windows Development Player，D3D11 / D3D12 | 可见窗口各 60 PASS、9 张图；构建 0 errors / 7 warnings | 所有变体均已覆盖，或 D3D12 在任意设备一定更快 |
| 首次 Windows 隐藏窗口运行 | 59 PASS，因两次查询环缓冲耗尽整体 FAIL；同条件复测 60 PASS | 隐藏窗口是唯一原因，或一次复测足以排除缺陷 |
| Android Development IL2CPP ARM64，Adreno 640，Vulkan | 两次各 60 PASS、9 张图；构建 0 errors / 9 warnings | 整个应用启动无错误，或性能达到移动端预算 |

`editor-results.txt` 记录实际 HLSL RNM 倾角契约、泡沫侧光/方差、FFT 生产 compute 与 bake/readback、Burst 查询和资源重建用例。
这补上了此前只有离线数值验证的部分：例如 RNM 在 0°/30°/60° 宏观倾角上保持约 26.565° 的细节偏转，泡沫 45° 侧光在桌面为 0.7071068、Android 为 0.7070313。
烘焙查询的 8192 点 Burst/managed 对照最大差为 0；CPU 原场查询与世界 XZ 逆查询的中位数分别为 0.0812 / 0.2527 ms，包含作业调度及等待。
后者说明逆查询有额外成本，不是整个船体系统的前后版本性能对照。

## 2. 已修复：查询回读积压时的所有权与恢复

实现：[QueryBase.cs](../crest/Assets/Crest/Crest/Scripts/Collision/QueryBase.cs)。
该路径服务 GPU 位移/流速查询；与上一轮的 CPU 烘焙 FFT 采样修正是不同的代码路径。

首次失败的 `windows-d3d11-initial-failure-player.log` 将错误定位到 `SegmentRegistrarRingBuffer.AcquireNew → QueryBase.UpdateQueries → OceanRenderer.RunUpdate`。
旧实现达到 7 个请求后只从列表删除最老项，随后继续提交；对应登记槽却没有同步释放，也没有真正取消原生回读。
槽池耗尽后仍可能复用正在被异步结果引用的登记表，造成点位与结果对应关系不可靠。
失败请求不按实际归属释放、持久查询压缩时仅移动区间而未移动坐标，也是同一生命周期中的缺陷。

现在的行为是：

- 固定 10 个登记槽分别由当前待提交批次、最多 7 个在飞请求、最近两代成功结果持有，按确切对象释放。
- 提交前先处理已完成请求，再检查容量；积压达到上限时暂停新的 dispatch/readback，保留最新待提交坐标及最近有效结果。
- 提交前预留下一批次的槽，提交失败则归还预留槽；已经提交的登记表不再被后续查询写入。
- 乱序完成时按提交序号接受较新的成功结果，失败和过时完成项只释放各自的槽，尚未完成的旧请求继续计数。
- 持久查询压缩同步复制坐标，用预分配数组避免区间重叠或字典遍历顺序导致错位。
- 登记容量包含每个法线的三个采样点；满容量时仍允许原 owner 原长度更新；失败不会先破坏已有登记。
- 在输出前检查返回区间容量，防止调用方增大输出数组后读到其他 owner 的旧结果或越界；速度输出同样检查两代区间。
- 清理先禁止新工作并释放 CPU 结果；本实例仍在飞的回读完成/失败后，再释放其两个 GPU buffer，支持重复清理和迟到回调。

没有引入逐帧同步等待或全局 `WaitAllRequests`。
这项修复限制积压时的工作和所有权错误，不能让停滞的 GPU 立即产生新水面数据：暂停期间返回的是最近有效结果，恢复后的第一个新回读才提供较新的查询数据。
如果原生请求一直不完成，其资源也会保留至回调完成；仍需在真实后台切换、隐藏窗口及反复启停海洋时检查资源回收。
默认两个 GPU buffer 合计约 96 KiB / provider，尚未计入原生回读资源；反复创建多个旧实例时，单实例请求上限不代表所有实例合计内存有全局上限。
本轮不重定义同一 owner 任意改变点/法线布局的接口语义；区间检查不能替代完整的布局元数据。

新增固定 CPU 开销为一个 `Vector3[maxQueryCount]` 暂存数组，默认 4096 点约 **48 KiB / provider**，以及每次实际提交时的有效坐标复制。
请求上限和槽数保持 7 / 10，没有新增模拟纹理或着色器采样。

## 3. 已完善：验证工具不能忽略启动错误

实现：[QualityRunner.cs](../tools/UnityQualityTests/QualityRunner.cs)。
当前仓库原有 Runner 在进入 Play 后才开始监听日志，且把包含 `UnityEditor.Search.SearchDatabase` 的异常直接排除。
这会遗漏启动错误，并可能让包含 Search 异常的运行通过。

现在在初始化前建立监听，在 Edit Mode 调用 `SearchService.IsIndexReady(null)`，提前触发这次固定 Unity 版本的默认索引初始化。
返回 false 仅表示索引尚未 ready，不会被当成失败，也不会无限等待整个索引构建。
Error / Exception / Assert 均计为失败，跨 Play Mode 的 domain reload 使用 `SessionState` 保留启动失败状态并恢复监听；同步启动异常也进入失败退出流程。
原有画面断言、比较阈值及运行流程保持原样。

这一小修正根据实机报告移植；附件中更广的 Player、Burst、GPU 契约测试源码尚不在当前仓库。
仍需由宿主检查完整 Editor/Player 日志，覆盖监听开始之前、重载间隙或进程退出时的错误。
API 依据：[Unity 6000.6 SearchService.IsIndexReady](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Search.SearchService.IsIndexReady.html)。

## 4. 泡沫：解析衰减正确，不代表每步半精度存储无误差

新增诊断：[check_foam_storage.py](../tools/check_foam_storage.py)。
格式说明：[SimSettingsFoam.cs](../crest/Assets/Crest/Crest/Scripts/LodData/Settings/SimSettingsFoam.cs)。

`editor-results.txt` 的生产泡沫 compute 测试使用初值 1、衰减率 0.8/s、时长 2 秒，无新源和 Flow，解析结果为 `exp(-1.6) ≈ 0.201896518`。

| 模拟频率 | RHalf 实测结果 | 相对解析值的偏差 |
| --- | ---: | ---: |
| 15 Hz | 0.2000732 | −0.903% |
| 30 Hz | 0.1981201 | −1.870% |
| 60 Hz | 0.1939697 | −3.926% |
| 200 Hz | 0.1762695 | −12.693% |

同一夹具的 RFloat 最大相对误差约 **0.000539%**。
读取实际 shader 表达式并在每次存储施加 float32 / half 向零舍入后，CPU 模型复现了日志打印精度下的全部八个测量值。
这是“每次回写累积量化”的证据；附件没有原始 GPU 位模式，不能据十进制日志宣称跨平台逐位一致。

Direct3D 的[浮点格式转换规则（第 3.2.2 节）](https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm)支持这一向零舍入解释；[Vulkan 的浮点格式转换规则](https://docs.vulkan.org/spec/latest/chapters/fundamentals.html)并未统一规定该舍入模式。
这张 GTX 1650 / D3D11 表不能直接当成 Adreno 的量化结果。
反例也说明简单改成 nearest-even 并不通用：200 Hz、0.02/s 的小衰减在 half 模型中会停在 1，而两秒后应为约 0.960789。

因此本轮保留默认 `R16_SFloat` 及现有 shader，新增精度诊断和 Inspector 提示。
对长寿命、低衰减或高频泡沫，需要评估已有的 `R32_SFloat` 选项；不要仅为消除这张表的误差，把所有移动端泡沫强制升为 R32。
两个历史 buffer 加一个临时 source 纹理，在 `384×384×7` 时，理论像素存储总量为 R16 **5.90625 MiB**、R32 **11.8125 MiB**，未计驱动对齐及其他资源。
翻倍的是这三张泡沫纹理的存储，并不等于整个帧时翻倍；带宽成本需在目标设备测量。
低频时没有正时间子步的一帧仍有 `dt=0` 重投影，因此也不能把 30→15 Hz 直接说成 dispatch 数减半。

## 5. Android：先建立可归因的移动预算

以下 GPU 中位数来自 `windows-d3d11-results.txt`、`windows-d3d12-results.txt` 和 `android-vulkan-results.txt`；单位为 ms。

| 场景 | Windows D3D11 | Windows D3D12 | Android Vulkan | Android 总帧间隔中位数 |
| --- | ---: | ---: | ---: | ---: |
| main | 4.27 | 1.93 | 53.02 | 50.11 |
| boat | 5.79 | 2.27 | 78.80 | 83.53 |
| BoatWakes | 5.90 | 3.06 | 107.93 | 100.31 |
| threeboats | 5.23 | 2.08 | 86.26 | 83.52 |
| Examples | 4.43 | 2.33 | 52.32 | 50.10 |
| LakesAndRivers | 6.52 | 2.21 | 193.96 | 200.42 |
| whirlpool | 4.41 | 2.05 | 89.70 | 83.52 |

Android 第二次运行的各场景 GPU 中位数变化均小于 0.53%，慢帧具有重复性。
这些是每场景 60 个 gameplay frame、60 FPS 上限下的 Development Player 短时样本；没有旧提交 A/B、逐 pass 时间或独立有效 GPU 样本计数。
Windows 大多受约 16.67 ms 帧间隔上限限制；Android 当前约 5–20 FPS，尚未达到 30 FPS 对应的 33.3 ms 预算。
Android 日志中的运行分辨率为 **2173×1017**，截图为 **960×540**；截图大小不能作为实际相机渲染目标大小，还应记录运行时 descriptor。

### 5.1 源码中已核实的测量入口

- [QualitySettings.asset](../crest/ProjectSettings/QualitySettings.asset) 六个等级共用 [CrestPipeline.asset](../crest/Assets/CrestURPSettings/CrestPipeline.asset)，没有实际划分 URP 移动画质。
- 当前 URP renderScale=1，主阴影 2048 / 4 级联 / 150 m；MSAA 已为 1，HDR precision 已为 32 位，不能把这些已经较低的选项再当成新增收益。
- [LakesAndRiversSceneCore.prefab](../crest/Assets/Crest/Crest-Examples/LakesAndRivers/Scenes/Internal/LakesAndRiversSceneCore.prefab) 的 LOD 数据为 768×768×7、几何降采样因子 2；main 为 384×384×7、因子 4。
- 按 [OceanBuilder.cs](../crest/Assets/Crest/Crest/Scripts/OceanBuilder.cs) 的 `tileResolution = 0.25 × lodDataResolution / geoDownSampleFactor`，两者标准块内部网格分辨率为 96 / 24；这不是实际总三角形数的实测比例。
- Lakes 还有多个 FFT 谱及 Terrain；LOD 数据分辨率不等于 FFT 分辨率，地形成本也不能记在海洋 shader 上。
- 船景共享 [SettingsWavesWakes.asset](../crest/Assets/Crest/Crest-Examples/BoatDev/Settings/SettingsWavesWakes.asset) 的 120 Hz 动态波；慢帧可能产生多个补步，应用已有 `LastUpdateSubstepCount` 记录实际数目。
- [CrestRenderer.asset](../crest/Assets/CrestURPSettings/CrestRenderer.asset) 的 RefractedLayers=512 会安排水前透明颜色拷贝；确定场景无此层依赖时，可单独测试 `RefractedLayers=0`。

上述设置是待测成本入口，不表示每帧都存在所有额外灯、阴影或反射工作。
当前示例未找到启用的平面反射组件；main / Lakes 材质默认未开细节法线 keyword，扩展 Runner 是否改动它们需补源码确认。
不能把平面反射或 RNM 默认视为这些日志的性能瓶颈。

### 5.2 第一组 A/B

选择 main、BoatWakes、Lakes 三个场景，固定相机路径并保持模拟时间正常推进，每项单独改变。
视觉 Runner 的冻结时间会改变 FFT 等实际工作，不能直接用于性能测量。

| 单独改变的项目 | 试验范围 | 同时验收 |
| --- | --- | --- |
| 屏幕分辨率成本 | renderScale 1 → 0.75 → 0.5 | 细泡沫、远场高光、岸线及水下边缘 |
| 主光阴影 | 2048 / 4 / 150 → 1024 / 2 / 60–80 m | 船岸接触、级联边界 |
| 海洋绘制成本 | 临时关闭 tile renderer，保留模拟 | 仅用于分离模拟与绘制成本 |
| 水前透明颜色拷贝 | RefractedLayers 512 → 0，仅无依赖场景 | 水前透明物折射；保留深度与 opaque 纹理 |
| Lakes 几何密度 | 几何降采样 2 → 4，LOD 数据分辨率先不变 | 浪形、岸线、LOD 接缝 |
| Lakes 模拟纹理 | 768 → 384，另行控制几何密度 | 河流细节、窄岸线与模拟分辨率 |
| 地形 | 单独隐藏作诊断；另测 pixel error 1 → 5 | 轮廓、遮挡、树与地表细节 |
| 船景动态波 | 120 → 60 Hz | 真实补步数、尾迹波速/能量及交互稳定性 |
| 3D 泡沫照明 | 单独关闭 `_FOAM3DLIGHTING_ON` | 保持泡沫覆盖，检查厚度与局部高光 |

改变海洋分辨率/几何配置应在独立副本中、海洋启用前完成。
移动档位可以从 renderScale=0.75、1024 主阴影、2 级联、60–80 m、几何因子 4 开始测试；这是一组测量起点，没有达到 30 FPS 的实测承诺。
不要先削减 LOD 数，因为它还改变水域覆盖和远场表现。

性能记录至少应包含实际 Quality/URP asset、相机 descriptor、LOD/FFT 激活情况、各模拟格式/频率/补步数、可见 tile/地形/灯、CPU 主线程/渲染线程/present wait 和 GPU 时间。
按 `frameStartTimestamp` 去重 GPU 样本，报告无效值及有效数量，截图/readback 放到采样窗口外。
依据：[FrameTimingManager.GetLatestTimings](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/FrameTimingManager.GetLatestTimings.html) 返回最近已完成帧，实际返回数可以少于请求数，不能每次查询都视为一个新帧。
最后在 Release Player 持续运行，复核温升后的 p95 和内存；当前短时数据不足以完成热稳定验收。

## 6. 截图需要更有判别力的视觉验收

已检查附件中的海面、RNM、泡沫及跨三后端水下 PNG。
海面图能看到浪面反射、近岸水色和泡沫，但单张图不能证明时间稳定性或真实度相对旧版提升。

**RNM：**`editor-rnm-current.png` 的水面大面积偏暗，缺少能显示陡浪细节方向的照明；全图差值约 1.86e-6 不能作为视觉收益证据。
应在 30°/60° 浪面使用明亮条纹反射环境或侧光，记录 keyword 与强度，保留旧/新成对图，并报告水面 ROI 的误差及变化像素比例。
已有 GPU 角度契约保留；平坦水面和 zero-detail 的不变端点也继续检查。

**泡沫：**`editor-foam-flow.png` 的纹理与高光可见，但 finite 检查及若干时刻的静帧不能证明相位交接平滑。
应补跨 Flow 周期、跨 LOD 的短序列，比较边界前后与正常运动阶段的泡沫 ROI 亮度变化，同时检查岸边与船尾覆盖的自然度。

**水下：**D3D11、D3D12 和 Android 的 underwater 图均大面积近黑、仅右上少量绿色，因此不应先归为 Android 专属 shader 故障。
仅凭这些图无法区分相机遮挡、散射/雾密度、光照和实现错误。
应新增无遮挡水槽，设置已知颜色的接收面在 1 / 5 / 15 m，分别测试零密度、低密度、原材质，比较 early/late underwater 的路径衰减与边缘混合。
不应通过全局抬亮水下材质来代替这项验证。

## 7. Android 启动与构建警告

两份 Android logcat 都在脚本引擎初始化之前记录：

```text
java.lang.ClassNotFoundException: com.google.android.play.core.assetpacks.AssetPackManager
```

其后 Vulkan、场景和 GPU 契约继续通过，但这不是整份进程日志无错误。
上传报告中的宿主工具已把该完整日志错误计入失败退出码，这个验收规则应保留。
当前 Crest 源码没有 AndroidAssetPacks 调用；[Unity AndroidAssetPacks 文档](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Android.AndroidAssetPacks.html)说明接口依赖 PlayCore，却不能确定此次原生启动探测的调用来源。
下一步应对照最小空工程与导出 Gradle 的依赖/插件、检查实际是否使用 Play Asset Delivery；需要 PAD 时按使用的 Unity/插件版本补齐依赖，不需要时定位并移除多余调用或配置。
不把添加一个未使用的 Java 库当成已验证修复。

源项目 Active Input Handling 为 Both；移动构建应选择实际使用的单一输入后端并回归船控、相机及 UI。
示例代码同时保留旧/新输入条件分支，因此本轮没有直接改变全工程输入后端。
构建结果文件只有 7 / 9 个警告的计数，缺少具体 shader 变体、文件行号及完整编译栈，不能据“可能未初始化背景变量”一句准确修复某个分支。
报告提及退出时的两条 JobTempAlloc 提示也没有 Crest 调用栈；还需完整构建/退出日志及反复场景切换的内存跟踪，不能据此宣称 Crest 泄漏或已清零。

## 8. 本轮验证与复测要求

以下是本轮新增代码的检查结果，不把用户的 `90a93293` GPU 数据计入通过数。

| 检查 | 结果 | 验证范围 |
| --- | --- | --- |
| 实际 `QueryBase.cs` 生命周期回归 | **21 / 21 通过** | 编译生产 C#，用可控的托管 Unity API 替身模拟调度、完成及数组访问 |
| 同一套查询回归运行在 `90a93293` | **4 / 21 通过，17 项失败** | 证实测试可检出旧行为；不是 GPU 性能比较 |
| 泡沫存储表征 | **7 / 7 通过** | 源码表达式、八个日志结果及 RTZ/RNE 边界与冻结反例 |
| 项目 C# 语法 | **146 文件 × 4 配置，0 错误** | 语法解析，不等于 Unity API 绑定 |
| 质量 Runner C# 语法 | **1 文件 × 4 配置，0 错误** | 同上；图像断言和阈值未改 |
| 静态源码及补丁检查 | **0 错误** | 101 个 shader 文件、186 个本地 include 引用、6 个 asmdef、manifest、metadata，及 `git diff --check` |

查询用例覆盖 100 帧积压、同步/乱序/失败/过期回读、两代结果与满槽共存、提交异常回滚、持久坐标压缩、容量和输出区间、清理后回调及实例隔离。
它们检查可观察行为而不依赖新实现的私有槽池字段；结果亦经独立代码审查与复跑。
托管替身只回显提交坐标以核验对应关系，不执行海洋 compute，不代替实际 Unity 的 NativeArray、GPU、驱动或隐藏窗口测试。

查询回归不需要 NuGet 包，使用 .NET 10 SDK：

```bash
python tools/check_query_lifecycle.py
# dotnet 不在 PATH 时：
python tools/check_query_lifecycle.py --dotnet /path/to/dotnet
# 下面应报告旧代码的 17 项失败并返回非零退出码：
python tools/check_query_lifecycle.py --ref 90a93293
```

泡沫表征可直接使用上传日志复现：

```bash
python tools/check_foam_storage.py --results /path/to/editor-results.txt -v
```

该命令 PASS 表示源码/量化模型与声明的夹具一致，不表示 RHalf 寿命误差已被修复。
没有新的 shader 修改，因此此前的 225 项离线编译和本次上传的实际 GPU 契约作为原有 shader 的证据保留，不伪装成本轮再次 GPU 执行。

下一次设备复测优先覆盖：

1. 新 `QueryBase` 在隐藏/后台、低帧率、GPU 回读延迟下的有界请求数，以及回到前台后的恢复。
2. 多 owner 交错移除、正常/异常完成和反复禁用/重建 OceanRenderer，检查浮力连续性及 buffer 回收。
3. Runner 首次创建 Search.index、注入 Search 错误及 domain reload 后的失败保留。
4. Adreno 上的 R16 / R32 泡沫精度与逐项性能 A/B。
5. 有可辨物体的水下图、陡浪 RNM 对比及泡沫相位短序列。

异步请求 API 依据：[Unity AsyncGPUReadbackRequest](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Rendering.AsyncGPUReadbackRequest.html)。
成功的回读数据仅有一帧有效期，需要及时复制到持久结果；资源的具体驱动回收行为仍由真机验证。
