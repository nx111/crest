# c685e51e 最新修改与实机报告复核

日期：2026-10-11。复核对象：[`c685e51edcce3b132d791b04bdd5611c70847f46`](https://github.com/nx111/crest/commit/c685e51edcce3b132d791b04bdd5611c70847f46)，父提交 `9df1db9f`。目标环境仍为 Unity 6000.6.2f1 / URP 17.6.0。

**结论：三处生产修复有源码和实机证据支持，予以保留。本次进一步修正诊断工具的假通过路径和报告表述；没有新增生产 Shader、场景或默认质量设置修改。** 新增工具检查只完成离线验证，不把既有实机通过数转记为新版工具的运行结果。

## 1. 版本与证据对应

本次逐条核对 SHA256，而不只依赖报告标题或目录名：

| 归档清单 | Git 对象 | 核对结果 |
| --- | --- | --- |
| [production-sha256.txt](_media/urp-realism/2026-10-11-9df1db9f/production-sha256.txt) | `9df1db9f:crest/<清单路径>` | 241 / 241 匹配 |
| [production-camera-fix-sha256.txt](_media/urp-realism/2026-10-11-9df1db9f/production-camera-fix-sha256.txt) | `c685e51e:crest/<清单路径>` | 242 / 242 匹配 |

最终清单多出的文件是 Android 构建兼容脚本。最终 Player/Editor 结果、构建及退出日志、实际屏幕截图和已安装工具快照均已入库。[实机报告](URP-UNITY-RUNTIME-RETEST-9df1db9f.md)中的相对证据链接也已核对存在。原始日志和两个 `tested-harness-*` 快照保持原样；当前工具修改与历史运行来源可以区分。

## 2. 三处生产修复的复核判断

### 查询退出清理：保留

[QueryBase.cs](../crest/Assets/Crest/Crest/Scripts/Collision/QueryBase.cs) 的普通 `CleanUp()` 仍立即释放 CPU 结果、异步等待本实例原生回读释放 GPU buffer。新增的应用退出、Editor 退出及程序集重载入口先清理，再对自身仍在飞的请求逐个 `WaitForCompletion()`，最后处理完成队列。遍历使用请求快照，允许回调修改原列表；没有使用全局 `WaitAllRequests()`。

不能把它表述成“所有路径都不等待 GPU”：最终退出路径有意同步等待，正常运行期间的清理保持非阻塞。Unity 官方说明 [WaitForCompletion](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Rendering.AsyncGPUReadbackRequest.WaitForCompletion.html) 会阻塞 CPU/GPU，返回后请求已完成；限制其执行位置是该实现的重要边界。

修复前的 [Editor 分配栈](_media/urp-realism/2026-10-11-9df1db9f/editor-fresh-index-positive.log) 指向 QueryBase 构造的两个 ComputeBuffer。修复后的 Editor、Windows 和 Android 完整日志有明确退出诊断 PASS，未再记录对应 QueryBase buffer 泄漏。托管回归 24 / 24 通过；相同 24 项运行于原始 `9df1db9f` 时新增退出检查有 3 项失败。托管替身测试与原生设备证据分别成立，不相互替代。

### URP 相机恢复：保留

[CrestUniversalRendererFeature.cs](../crest/Assets/Crest/Crest/Scripts/URP/CrestUniversalRendererFeature.cs) 现在分别保存原始 projection 与 GPU projection：原始矩阵用于 `SetViewProjectionMatrices`，GPU 矩阵用于 inverse P / inverse VP；另恢复 URP 的 world-to-camera 手性及对应逆矩阵。

复核时逐项对照了安装的 URP 17.6 `ScriptableRenderer.SetCameraMatrices` 非 XR 分支。该实现正是使用原始 projection 更新 native camera state，再分别处理 GPU inverse；`UniversalCameraData.GetProjectionMatrix()` 的 jitter 也没有被本次恢复丢弃。

亲自查看[修复前实际屏幕](_media/urp-realism/2026-10-11-9df1db9f/android-before-camera-fix-device-gameplay.png)与[修复后实际屏幕](_media/urp-realism/2026-10-11-9df1db9f/android-final-screen-after-offscreen.png)：天空中的倒置地形已经消失，岛屿正确位于水面。最终离屏请求前、后的实际屏幕及 Windows D3D11 / D3D12 主窗口一致支持这一结论。离屏图像正常本身不足以排除主帧缓冲错误。

已验证范围是报告中的普通 Base Camera 和对应图形 API。XR、任意相机栈、动态 RTHandle 和特殊 viewport 没有因此获得完整验收；当前没有它们触发新增回归的证据，也没有据此继续改动生产矩阵代码。

### Android 启动依赖：当前工程保留

[AndroidAssetPackDependency.cs](../crest/Assets/Development/Editor/AndroidAssetPackDependency.cs) 仅在 Unity 6000.6.2f1、Android 构建时补缺少的直接 Asset Delivery 依赖。

[无依赖空工程日志](_media/urp-realism/2026-10-11-9df1db9f/android-empty-no-dependency-logcat.txt)在没有 Crest、URP、插件的条件下仍有同一个 `AssetPackManager` 缺类；[加依赖的空工程日志](_media/urp-realism/2026-10-11-9df1db9f/android-empty-with-dependency-logcat.txt)正常启动和退出。[最终 Crest 日志](_media/urp-realism/2026-10-11-9df1db9f/android-final-logcat.txt)也没有这条错误。因此该修复的归因和对照成立，没有把 Unity 启动问题归到海水 Shader。

`asset-delivery:2.3.0` 与 [Google 官方接入说明](https://developer.android.com/guide/playcore/asset-delivery/integrate-java)一致。这个示例 hook 只识别生成 Gradle 文件中直接声明的依赖，不覆盖旧 monolithic Play Core、传递依赖、本地 AAR 或其他后置 hook。已有 Play/PAD 插件的游戏移植此补丁时，应核对最终依赖图并按 [Google 迁移说明](https://developer.android.com/guide/playcore#common-migration-steps)移除旧 Play Core；当前仓库没有该冲突的触发证据。

## 3. 本次发现并修正的验收缺口

### Android 宿主可能在未完成退出时报告成功

旧 `run_android_audit.py` 看到托管 `RESULT: PASS` 后等待最多 30 秒，但到期仍未退出不计失败。最终文件再次读取失败或为空，也可能不包含 `FAIL` 而返回 0。`-crestQuitAudit` 未要求退出回调的阳性记录。空工程 driver 的 100 秒退出等待也有同样的到期放行问题。

另外两条具体路径会遗漏日志：仅保留退出段的 logcat 仍满足“含 Unity 行”；主进程被重启后，周期 PID 查询会覆盖旧 PID，最终只验收新进程。对后者，用实际 driver 与 fake ADB 复现了 `4410 → 5522`，最终只取 `--pid=5522` 且 `driver_exit=0`。

当前 [共享验收代码](../tools/UnityQualityTests/android_audit_checks.py)与两个 host 脚本要求：

- 进程必须在期限内确认消失；ADB 查询失败不是进程退出。
- Crest 最终文件必须读取成功、仅有一个 `RESULT: PASS`，且没有失败断言。
- 启动日志必须保留早期 `Context Type: ActivityOrService`。它在归档里早于缺类探测；`InitializeScriptEngine`、`Initialize engine version` 均晚于缺类错误，不能代替这个启动锚点。
- 开启退出诊断时，最终文件和完整 PID 日志必须分别含退出清理通过记录，且没有退出 FAIL。
- Crest 固定首次观测 PID；在周期观察、结果出现、后台恢复及等待退出时遇到 PID 替换即失败，继续保留原进程日志。
- 保存 `acceptance-errors.txt` 解释宿主拒绝原因，同时保留原有日志与结果产物。

这不推翻本轮最终 Android 阳性样本：归档确有早期启动、退出检查 PASS、native teardown 和 `Quitting process`，最终结果文件也含退出断言。新门禁用来阻止未来同类失败被漏报。

Editor / Windows 验收同样必须在进程结束后检查最终结果文件和完整日志。`CREST_QUALITY_RESULT PASS`、`CREST_PLAYER_AUDIT PASS` 或 exit 0 可能早于 quitting 回调，不能单独代表退出资源检查通过；工具说明已明确此规则。

### 生产速度检查会接受未计算的速度

旧 [RuntimeQueryAudit.cs](../tools/UnityQualityTests/RuntimeQueryAudit.cs) 使用 `RetrieveSucceeded(status)` 并检查输出有限。生产 API 的该函数只排除位移取回失败，仍允许速度标记 `NotEnoughDataForVels=4`、`VelocityDataInvalidated=8`、`InvalidDtForVelocity=16`；初始化为零的速度也满足有限值。

使用实际 QueryBase 与现有托管回读替身，提供两代合法平水位移，但两代时间戳相同，再调用公开 Query，得到了明确反例：

```text
status=16; InvalidDtForVelocity=16; auditAccepted=True; untouchedVelocity=True
```

工具现在每次调用前把位移、法线、速度全部写成 NaN，并要求 `status == QueryStatus.OK`，然后才检查有限值及单位法线。没有修改生产 Query API。该反例证明旧门禁有缺口，不能据此断言归档实机速度结果已经错误；新版条件仍需真实 Unity 复跑。

### 独立泡沫精度标签缺少实际格式核验

原报告的“32×32、无过滤”表述不准确：诊断执行生产 `UpdateFoam → SampleLod → linear sampler`。均匀输入和零 Flow 没有提供可观察的空间插值变化，不能据此称其不使用过滤。

归档的小纹理诊断只记录请求的 `RFloat/RHalf`，没有记录实际 `graphicsFormat`，也没走 manager 的 `LoadStore | Sample | Linear` 兼容选择。它不能证明整场景 R32 支持，也不能定位兼容回退具体由哪项 usage 导致。本次已纠正报告及工具说明。

[PlayerRetest.cs](../tools/UnityQualityTests/PlayerRetest.cs) 现在逐一确认两张 ping-pong 纹理创建成功并记录实际格式；只有实际均为 `R32_SFloat` 或 `R16_SFloat` 才进行对应标签的精度诊断。格式回退明确 SKIP，R32 的误差阈值仍为 `0.001`。没有修改衰减 kernel、误差阈值、性能 A/B 或默认纹理格式。

## 4. 实机数字支持的结论及限制

| 最终生产证据 | 归档结果 | 本次复核判断 |
| --- | --- | --- |
| Editor 完整质量 / GPU 查询 | 376 PASS，55 PNG | 结果及退出日志一致 |
| Windows D3D11 / D3D12 实际主窗口 | 各 129 PASS，exit 0 | 屏幕地形方向正常，对应查询 buffer 退出提示消失 |
| Windows 连续主窗口帧 | 各 130 PASS，1,567 / 1,568 帧 | 可核对摘要、代码、日志和三帧图；未取得原始完整帧包重新计算 |
| Android Vulkan 最终包 | 182 PASS，23 PNG，driver exit 0 | 归档含早期启动至退出记录，无所检查错误；后台恢复与退出检查通过 |

所有数字来自[归档复测报告及原始文件](URP-UNITY-RUNTIME-RETEST-9df1db9f.md)，不是本次复核重新运行 Unity 所得。

Windows 最大四个候选变化位于 580 / 590 / 600 / 610 帧，与工具四次 `ocean.enabled=false; yield return null; ocean.enabled=true` 及已保存三帧图吻合。这能解释所记录的一帧海水消失，不能证明所有高光或泡沫都稳定。320×180 JPEG、全图三帧差分和固定相机筛选会漏掉局部细节及移动相机问题。两个 `game-frames.zip` 仍只在测试者本地；后续应给完整帧包保存 SHA256 及可访问的产物地址，便于独立复算。

### Android 性能仍需单独优化

最终 [Android 原始结果](_media/urp-realism/2026-10-11-9df1db9f/android-final-results.txt)有 21 条 PERF，每条 120 个独立 GPU 时间戳样本、0 invalid；模拟与船体继续运动，相机控制固定。实际 descriptor 与表中尺寸吻合。

| 场景 | scale 1.0 GPU 中位数 | scale 0.5 GPU 中位数 | scale 0.5 帧间隔中位数 |
| --- | --- | --- | --- |
| Main | 57.757 ms | 29.980 ms | 33.403 ms |
| BoatWakes | 105.290 ms | 53.903 ms | 50.100 ms |
| Lakes | 160.356 ms | 127.763 ms | 133.592 ms |

只有 Main 的该视角降到 scale 0.5 后约 30 FPS。BoatWakes 将动态波降到 60 Hz，substeps 从 12 降至 6，全分辨率 GPU 仍为 93.226 ms；Lakes 降模拟 LOD 至 384、保持原 tile 网格密度，仍为 132.203 ms。单项变化均未建立 BoatWakes / Lakes 的合格移动档位。

这些结果支持下一轮分别定位像素、模拟和场景绘制成本；目前没有证据应只靠缩阴影或关闭折射层解决全部预算。相机修复改变可见地形和深度，不能把修复前后两张性能表相减后称为优化或回归。Development 短时 A/B 也不替代 Release、热稳定和长时间峰值。

### 仍保留的误差和警告

- 请求 RHalf 的 15 / 30 / 60 / 200 Hz 独立衰减误差约为 −0.903% / −1.870% / −3.926% / −12.693%。半精度高频寿命问题没有在这次查询/相机修复中解决；下次用新增实际格式日志复核标签。
- 整场景 R32 请求实际得到 `R16G16B16A16_SFloat`，归档已正确 SKIP R32 成本对照。不能把该设备结果用于宣称 R32 的整场景性能。
- Windows 构建器退出的两条 JobTempAlloc 提示仍存在。已取得的原生栈为 `LoadSerializedSubProgramFromCache → ShaderCompilation::CompileVariantJob → ujob_execute_job`，不能再称为没有调用栈，也不能计入已消除的 QueryBase buffer 问题。
- D3D12 启动的 info queue `0x80004002` 警告仍保留；实际渲染和查询回收通过不等于该警告已经修复。

## 5. 本次验证与下一轮最小复测

| 本次执行 | 结果与边界 |
| --- | --- |
| Android 宿主回归 | 15 / 15 通过；真实 Python CLI + fake ADB，含退出超时、传输失败、最终文件缺失/失败、日志截断、退出标记及 PID 替换反例 |
| 原始 Android 日志内容回放 | 最终 Crest 与加依赖空工程满足内容门禁；未加依赖空工程被正确拒绝；不代表重新观测设备退出 |
| 生产 QueryBase 托管回归 | 24 / 24 通过；本次生产源码未变 |
| C# 诊断工具语法 | 12 个文件 × 4 个预处理配置，0 语法错误；不等于 Unity API 绑定或 GPU 编译 |
| Git 差异检查 | `git diff --check` 通过；生产目录及历史测试快照没有本次修改 |

复现命令（仓库根目录，已安装 .NET 10 SDK 时可省略第一条的 `--dotnet`）：

```text
python tools/check_query_lifecycle.py --dotnet <dotnet-path>
python -m unittest discover -s tools/UnityQualityTests -p test_android_audit_checks.py -v
dotnet run --project tools/StaticChecks -- tools/UnityQualityTests
git diff --check
```

本次没有可用 Unity Editor / GPU / Android 设备，未执行新的设备编译或画质验收。下一轮先在停止的临时测试项目更新当前工具、重新编译，使用 `-crestQueryAudit -crestQuitAudit -crestFoamStorage -crestScreenAudit` 核对严格速度条件、实际格式与完整退出门禁；Android 加 `-crestBackgroundAudit`。性能和连续帧分别运行，避免录制开销混入 A/B。

实际屏幕朝向目前仍需要人工查看；现有“PNG 文件存在”的断言不验证地形方向。若要自动化，应使用固定相机与已知方向标记的专用场景。画质下一步优先补移动相机、无损或高分辨率水面 ROI 的泡沫/高光序列，再据图像和帧时决定生产调整；当前短时缩略序列不足以指导新的抗闪烁算法。
