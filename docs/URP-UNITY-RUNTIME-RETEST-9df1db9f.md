# Unity / Player 复测：9df1db9f

复测对象是 `9df1db9f5993432f127e9ac5b60e12925141910a`，在 `90a93293` 实机基线之后的新查询生命周期与 Runner 启动修复。
先测试原始提交，再根据用户“修复发现的明显错误”的要求修复并复验。原始基线的两个缓存测试副本均核对 241 个含示例的代码/Shader 文件 SHA256，其中 Crest 核心生产文件为 213 个；相对 90a93293 只更新了 QueryBase.cs 和 SimSettingsFoam.cs。基线日志及当时测试工具快照已单独保留；本地修复的文件清单另存于 Retest-9df1db9f-local-fix。
Unity 为 6000.6.2f1，URP 17.6.0。Windows 使用 GTX 1650，Android 使用 Samsung SM-G9750 / Adreno 640 / Android 12。
所有 Unity 构建串行执行，复用原有 Library 与 shader cache。

## 本地修复与最终复验

已完成的生产修复包括：QueryBase 在应用退出和编辑器重载时等待自身的在飞请求并释放 GPU buffer；为 Unity 6000.6.2f1 Android 项目补足官方 Asset Delivery 依赖；URP 恢复相机时使用原始 projection 更新 native camera state，并分别恢复 GPU inverse 矩阵。

| 最终检查 | 结果 |
| --- | --- |
| 生产 QueryBase 托管回归 | 24/24 PASS；原始 9df 在新增退出检查上 3 FAIL |
| Editor 完整质量与真实 GPU 查询 | 376 PASS、55 PNG；立即退出资源检查 PASS |
| Windows D3D11 / D3D12，含实际主窗口截图 | 各 129 PASS、exit 0；无 ComputeBuffer GC / unreleased buffer 提示 |
| Windows D3D11 / D3D12 连续主窗口帧 | 各 130 PASS、exit 0；分别 1,567 / 1,568 帧，无漏帧或回读错误 |
| Android Vulkan 完整复验 | 182 PASS、23 PNG、driver exit 0；启动到退出完整日志 0 errors；后台恢复、立即退出资源检查 PASS |

各轮诊断参数不同，断言总数不同。三平台的查询回收日志已通过；Windows 构建器退出的原生 JobTempAlloc 提示、D3D12 启动 info queue 警告仍保留。Android 整场景 R32 泡沫成本比较因实际格式回退而 SKIP，未把半精度成本计作 R32 数据。

## 原始 9df 基线（修复前）

| 检查 | 结果 | 边界 |
| --- | --- | --- |
| 生产 QueryBase 的托管生命周期回归 | 21/21 PASS | 可控 Unity API 替身，非实际 GPU |
| 相同托管回归运行于 90a93293 | 4/21 PASS，17 FAIL | 支持旧代码存在对应缺陷 |
| 泡沫存储模型 | 7/7 PASS | 表征量化误差，不是修复误差 |
| Editor D3D11 扩展质量检查 | 373 PASS、55 PNG、exit 0 | 含真实 GPU 查询与重建；退出日志另有资源提示 |
| 新建 Search.index 正常启动 | 180 PASS、exit 0 | 这次未开启扩展和 specular AA 参数 |
| Search 启动错误注入 | 控制测试 PASS、进程 exit 1 | 错误跨 Play Mode domain reload 保留，Runner 正确 FAIL |
| Windows D3D11 隐藏窗口 | 126 PASS、exit 0 | GPU 帧时不可用；不据此评估性能 |
| Windows D3D11 可见窗口 | 126 PASS、exit 0 | 进程退出另有 ComputeBuffer GC 提示 |
| Windows D3D12 可见窗口 | 126 PASS、exit 0 | 进程退出另有未释放 GPU 资源提示 |

这张表的 PASS 指各托管断言通过，不等于整个原生进程日志无错误或资源回收验收通过。

## 查询队列与回收

真实 GPU 诊断执行生产 QueryBase，并通过一个专用坐标回显 compute kernel 核对 owner 数据；另外实际执行生产 QueryDisplacements 水面 kernel。
坐标回显 kernel 不是海洋波浪算法替身，它用于独立确认映射、缓冲和原生回读；水面查询另行检查有限位移、单位法线和有限速度。

100 次同帧连续提交实际达到 7 个待处理请求。继续提交保持有界，随后取回最新坐标。
输出数组增大时，旧结果范围不足会返回失败，哨兵输出保持不变。
12 轮多 owner 批次保持请求数不超过 7。
8 次带在飞请求的 provider 清理，CPU 结果立即释放，原生回调结束后两个 GPU buffer 变为 null。
4 次启用“禁用时销毁资源”的 OceanRenderer 重建，旧 provider 的 GPU buffer 释放，新 provider 正常创建。
本轮 Windows 三次运行没有出现 `Query ring buffer exhausted` 或 registrar pool 无空槽错误。

## 基线退出资源回收问题及本地修复

Editor 直接退出时出现 `Leak Detected : Persistent allocates 2 individual allocations`。
开启 NativeLeakDetection stack trace 后，两个分配明确来自 QueryBase 构造中的 ComputeBuffer（生产源码第 158、159 行），不是 CPU NativeArray。
详见 [Editor 退出调用栈](_media/urp-realism/2026-10-11-9df1db9f/editor-fresh-index-positive.log)。

D3D11 可见 Player 退出时出现 4 条 `GarbageCollector disposing of ComputeBuffer`。
D3D12 可见 Player 除同样的 4 条 GC 提示外，另有 4 条 `Unreleased non-evictable resource`，每个驱动分配 65536 B，名字均含 `Buffer-16-49152`。
49152 B 与默认 4096 × 12 B 的查询 buffer 负载相符；结合 Editor 分配调用栈，这是退出路径需要继续完善的实际证据。
尚未对每一个 Player buffer 逐对象归属追踪，因此不把所有退出分配直接计为某个特定 provider。

原始 9df 运行期间的清理/重建通过，直接进程退出的清理失败。
原始实现等在飞请求的回调释放 GPU buffer；应用退出、Player Loop 不再正常推进时，无法保证之后还会收到回调。
本地 QueryBase 修复已增加 Application.quitting 以及编辑器退出、assembly reload 的回收入口。普通 CleanUp 仍不等待 GPU，退出入口先释放 CPU 结果，再只等待本实例仍在飞的请求完成；GPU buffer 释放后移除事件订阅。已清理但仍有请求的实例保留退出处理器，避免退出时丢失释放机会。Shader 加载失败也会回收已经分配的资源。

托管回归已扩展到 24 项并全部通过，新增退出待处理请求、清理后退出、只等待自身请求且不依赖后续回调三个行为检查。实际 Unity 与设备的修复后完整退出日志另行记录，不能用托管检查替代原生验收。

D3D12 还保留启动时的 info queue 查询失败（0x80004002），随后实际 D3D12 场景和 GPU 契约继续运行通过。
Windows 构建成功、0 错误、7 警告，退出有两条 JobTempAlloc 提示；它们未提供与以上 QueryBase 分配相同的调用栈，分别记录。

## Runner 启动修正

首次复测未进入渲染阶段，打开场景时捕获 4 条 Unity.Hierarchy.HierarchyViewModel.SetState 的 `manager != NULL` 断言，并正确 exit 1。
[首次失败结果](_media/urp-realism/2026-10-11-9df1db9f/editor-initial-hierarchy-failure-results.txt) 已保留。

本地只修改测试 Runner：日志监听立即开始，管线/场景初始化改到 EditorApplication.delayCall；Editor 诊断以可见窗口启动。
[Unity delayCall 文档](https://docs.unity.com/en-us/engine/6000.6/script-reference/unityeditor/editorapplication/delaycall)规定回调在 Inspector 更新后执行。
调整后完整检查与再次创建 Search.index 的检查均通过，无 Hierarchy 断言；第一次因隐藏窗口等待 UI 更新较慢，后续使用 Normal 窗口。
刻意注入的 SearchDatabase 前缀错误栈在 domain reload 后保留，并使 Runner FAIL，控制进程 exit 1。
该启动修正只影响诊断工具。默认泡沫格式和质量配置保持提交内容；生产查询回收和 URP 相机状态另行修复并复验。

## Android 原始基线实机与性能

原始提交构建成功，0 错误、6 警告。实机查询积压、8 次待处理清理、4 次 Ocean 重建与七个场景的渲染检查通过；完整运行 FAIL，原因有两处：启动缺少 AssetPackManager，以及 R32 对照未成功切换实际纹理。

R32 对照错误来自测试工具修改了 LodDataMgr 的隐式默认设置：自动重建销毁该默认对象并重新创建 R16 默认值。工具已改为暂时指定一个 settings 实例，使 R32 格式能够跨重建保留，随后恢复原有设置。默认 R16 格式没有自动升级为 R32。

| main 配置 | 实际相机尺寸 | GPU 中位数 ms | 总帧间隔中位数 ms |
| --- | --- | --- | --- |
| renderScale 1.0 | 2173×1017 | 53.162 | 50.115 |
| renderScale 0.75 | 1629×762 | 38.763 | 33.414 |
| renderScale 0.5 | 1086×508 | 28.670 | 33.407 |
| 恢复 1.0 | 2173×1017 | 53.361 | 50.113 |
| shadow 1024/2 cascades/70 m | 2173×1017 | 52.690 | 50.110 |
| RefractedLayers 0 | 2173×1017 | 52.063 | 50.113 |

分辨率 A/B 支持该场景主要受 GPU 像素开销限制。只缩小阴影或禁用折射层的改善有限。Development、设备驱动、视角及短采样窗口都是测量条件；不能推断所有场景都达到 30/60 FPS。

实机生产泡沫 kernel 的两秒衰减（0.8/s，初值 1）中，R16 在 15/30/60/200 Hz 的相对误差为 -0.903%/-1.870%/-3.926%/-12.693%；R32 最大相对误差约 0.000536%。R16 有限值检查通过，仅表征误差存在；高频 R16 的寿命误差没有因查询修复而消失。

启动错误另用真正空工程（无 Crest、URP、插件）进行对照。Unity 自带 classes.jar 的 PlayAssetDeliveryUnityWrapper 构造器会加载 AssetPackManager，捕获 ClassNotFoundException 后直接记 error。原始 APK 的 unityLibrary/build.gradle 未声明 asset-delivery。该定位来自本地 Unity Java 字节码与实际日志。

空工程首次运行 PID 15806，正常完成但完整日志包含同一条 ClassNotFoundException；加依赖后的运行 PID 17512 正常完成、完整日志 0 errors。两个包均 ARM64 IL2CPP/Vulkan，构建 0 errors / 1 warning。依据 [Google 官方接入文档](https://developer.android.com/guide/playcore/asset-delivery/integrate-java)，示例项目的 Development/Editor/AndroidAssetPackDependency.cs 在生成 Gradle 后补入 `com.google.android.play:asset-delivery:2.3.0`，仅对实测 Unity 6000.6.2f1 生效，已有直接依赖时不重复添加。这是项目构建兼容修复，不是海洋 shader 修复，也没有启用资源分包。
测量使用 Development / ARM64 IL2CPP / Vulkan，保持模拟时间推进，120 帧/配置，GPU 样本按 frameStartTimestamp 去重。
实际相机 descriptor 从 Crest render pass 观察，PNG 输出仍为 960×540，不能替代运行目标尺寸。

## 修复后的复验

| 检查 | 结果 |
| --- | --- |
| 24 项托管生命周期：原始 9df1db9f | 21 PASS / 3 FAIL，新增退出检查失败 |
| 同一组检查：本地 QueryBase 修复 | 24/24 PASS |
| Editor 原生查询、立即退出 | 56 PASS，CREST_QUERY_QUIT_AUDIT PASS；完整日志无 Persistent leak / ComputeBuffer GC |
| Windows D3D11 隐藏窗口修复后 | 113 PASS，立即退出资源检查 PASS，完整日志无 ComputeBuffer GC |
| Windows D3D11 可见窗口修复后 | 181 PASS、exit 0；含泡沫精度、三个场景 A/B、实际 R32 分配；完整日志无 ComputeBuffer GC |
| Windows D3D12 可见窗口修复后 | 129 PASS、exit 0；含泡沫精度，立即退出资源检查 PASS；无 ComputeBuffer GC / unreleased buffer 提示 |
| 空 Android：未补依赖 | 1 条启动 ClassNotFoundException |
| 空 Android：补入官方依赖 | 0 条完整日志错误、正常完成退出 |

Editor 退出诊断刻意保留两个各有 7 个待处理原生请求的 provider，其中一个已经调用 CleanUp。退出事件中验证二者 CPU/GPU 资源与请求全部释放，不依赖后续 Player Loop。更严格的 owner 压缩检查也等待索引真正压缩后的请求代次，而不是接受旧保留结果。
修复后代码指纹为 242 个文件：原有 241 个加 Android 项目构建兼容脚本，核心 shader 文件未变。Windows 构建 0 errors / 7 warnings；构建器退出的两条 JobTempAlloc 提示仍存在，它们没有相同的 QueryBase 分配栈，不能算入已修复的查询 buffer 问题。D3D12 的 info queue 接口查询失败也仍保留，实际 D3D12 渲染及回收通过。各轮参数不同，因此断言总数不同。Crest Android 最终完整包构建 0 errors / 6 warnings，设备验收详见下节。

### Android 首次修复包发现的补充问题

21:32 的 Crest 修复包完整设备日志已无启动缺类错误，查询检查及七场景离屏截图通过，但 R32 整场景对照仍 FAIL，后续后台/立即退出诊断尚未执行。该失败日志单独保留，不能把这一轮列为完整 PASS。

工具已进一步记录 requested / compatible / actual 格式，并确认临时 settings 仍被当前 manager 使用。若平台兼容回退不是 32 位浮点格式，明确 SKIP 整场景 R32 成本比较，不把它当作真实 R32 数据。32×32 原生 compute 衰减结果与整场景过滤、读写纹理的格式支持条件不同。

手机真实屏幕出现岛屿几何在天空中翻转，而同状态的 SingleCameraRequest 离屏截图正常。检查本地 URP 17.6 ScriptableRenderer.SetCameraMatrices 发现它使用非 GPU 的原始 projection 调用 SetViewProjectionMatrices，另行设置 GPU projection 的 inverse，并明确说明直接 SetViewAndProjectionMatrices 有 y-flip / winding 问题。Crest 原 RestoreCamera 只覆盖 global GPU 矩阵；本地已改为遵循 URP 的相机状态恢复，同时保留正确 inverse 及 world-to-camera handedness。该修改需要同时验收手机屏幕和离屏截图。

修改后完整 Editor 检查 376 PASS / 55 PNG，立即退出诊断 PASS，无 QueryBase buffer 泄漏提示。最终 Windows D3D11 / D3D12 各 129 PASS、exit 0，实际主窗口与离屏图像的地形方向均正常，完整日志无查询 buffer 回收提示。

Android 最终主场景的实际屏幕在离屏请求前、后各保存一张 2173×1017 PNG，均确认岛屿位于水面、天空中没有倒置地形。此前错误画面来自手机实际帧缓冲，不能用离屏图像正常替代此项验收。最终查询积压、重建、后台恢复与立即退出检查均通过，完整进程日志无启动缺类、ComputeBuffer GC、未释放 buffer 或运行错误。

![修复后的 Android 实际主屏幕](_media/urp-realism/2026-10-11-9df1db9f/android-final-screen-after-offscreen.png)

JobTempAlloc 的诊断调用栈已定位为 Unity 原生 LoadSerializedSubProgramFromCache → ShaderCompilation::CompileVariantJob → ujob_execute_job，分配只存活 1–3 个 editor frame。曾测试构建器等待至少 16 次 update、一秒及 ShaderUtil.anythingCompiling=false 后退出，提示仍存在，实验已撤销。此项是未消除的 Unity 原生构建退出问题；未屏蔽日志，也不将其算作 QueryBase 回收问题。

## Windows 四次闪烁的定位

用户报告测试中有东西“一闪而过”。补跑实际主窗口连续帧记录后，D3D11 记录 1,567 帧、27.91 秒，D3D12 记录 1,568 帧、28.47 秒；两轮均无帧号缺口、回读错误或运行错误。
两个后端都在第 580、590、600、610 帧出现一帧海水消失、海底地形露出的变化，下一帧恢复。它们全部处于查询清理/重建阶段，与 RuntimeQueryAudit 固定四次 `ocean.enabled = false; yield return null; ocean.enabled = true` 一一对应。
这是测试主动关闭海洋一帧的预期效果；正常游戏不会自动执行这组诊断。其余记录及新增 360 帧 Main 稳定运行阶段未观察到同类单帧闪现。短时记录不能排除所有高光闪烁、长时间或其他视角的异常。

![D3D11 重建测试前、中、后相邻三帧](_media/urp-realism/2026-10-11-9df1db9f/windows-d3d11-rebuild-single-frame.png)

图中原始 GPU 回读行序已转为正常显示方向；原始 JPEG 帧包保留不变。D3D12 得到相同的四个变化帧，统计与相邻帧分别保存。图像排序工具只找出候选变化供目视检查，不把低差异作为“无闪烁”的通用证明。

录制工具首次编译的 CompressionLevel 命名冲突已修正。首次把连续录制和单张截图同时启用时，Unity 两次报告截图失败，因此该轮 FAIL 被单独保留；分开运行后两后端完整通过。工具现在在连续录制时跳过单张截图，避免同帧调用冲突。

Android 首次主窗口截图检查发现另一处工具错误：ScreenCapture 自动追加 persistentDataPath，传入绝对路径造成目录重复并报两次存储失败。该轮主动停止、日志保留，未计为通过。已修正为应用目录内的相对路径，并增加两张文件的保存检查；生产代码与此前指纹一致。

## Android 最终结果与性能预算

最终运行目录为 `AndroidCaptures-vulkan-20261011-222518`，应用 PID 4410，182 PASS / 23 PNG，driver exit 0。单独保存的 app PID 日志包含启动记录和 `CREST_QUERY_QUIT_AUDIT PASS`，从启动到进程结束的错误计数为 0。没有忽略启动错误或退出分配提示。

Host 实际按 Home 进入后台，5 秒后恢复同一应用；pause transitions=2、查询积压峰值=4，恢复后取得有效生产波浪查询结果。立即退出时另行创建两个各有 7 个原生待处理请求的 provider，其中一个已 CleanUp；退出事件验证二者 CPU/GPU 资源和请求均释放，应用进程随后消失。

R32 请求实际得到 `R16G16B16A16_SFloat`，与 manager 的 compatible format 一致，临时 settings 跨重建保持有效。因此明确 SKIP 整场景 R32 成本比较。单独 32×32、无过滤的 compute 衰减仍给出先前相同的 RFloat / RHalf 精度表征；它不证明整场景过滤纹理支持 R32，R16 的高频寿命误差也未修复。

下表是相机修复后本次设备测量，每个配置采样 120 帧，GPU 样本按时间戳去重，所有配置均 120 个有效样本、0 invalid。Main / BoatWakes 为 HDR `B10G11R11_UFloatPack32`，Lakes 为 `R8G8B8A8_SRGB`，均实际 MSAA 1。模拟与船体继续运动，相机控制固定；Development Player、60 FPS cap。

| 场景与配置 | 实际尺寸 | GPU 中位数 ms | 帧间隔中位数 ms |
| --- | --- | --- | --- |
| Main scale 1.0 | 2173×1017 | 57.757 | 50.125 |
| Main scale 0.75 | 1629×762 | 41.653 | 50.089 |
| Main scale 0.5 | 1086×508 | 29.980 | 33.403 |
| Main 恢复 scale 1.0 | 2173×1017 | 57.914 | 50.128 |
| BoatWakes scale 1.0 | 2173×1017 | 105.290 | 100.195 |
| BoatWakes scale 0.75 | 1629×762 | 78.746 | 83.488 |
| BoatWakes scale 0.5 | 1086×508 | 53.903 | 50.100 |
| BoatWakes dynamic waves 60 Hz，scale 1.0 | 2173×1017 | 93.226 | 100.159 |
| Lakes scale 1.0 | 2173×1017 | 160.356 | 166.990 |
| Lakes scale 0.75 | 1629×762 | 143.765 | 150.292 |
| Lakes scale 0.5 | 1086×508 | 127.763 | 133.592 |
| Lakes geometry factor 4，LOD 768 | 2173×1017 | 153.233 | 150.319 |
| Lakes LOD 384，geometry factor 1，保持原 tile 网格密度 | 2173×1017 | 132.203 | 133.625 |

Main 降至 scale 0.5 时该视角约 30 FPS；BoatWakes 和 Lakes 尚未达到 30 FPS。缩小阴影或关闭折射层的改善都有限：Main 56.993 / 56.380 ms，BoatWakes 104.622 / 104.468 ms，Lakes 160.328 / 159.687 ms。BoatWakes 将动态波频率降低至 60 Hz 后，记录的每帧模拟 substeps 从 12 降至 6，GPU 成本下降，但全分辨率仍不达预算。

Lakes 的仅降分辨率结果仍很慢，减少模拟 texel 密度比本轮单独减少网格密度更有效；上述单项配置均不足以建立 30 FPS 移动端配置。需要后续针对目标场景的模拟、网格和地形成本调优。本轮没有把这些诊断参数写回默认场景或资产。

此前基线存在实际地形翻转，相机修复改变了可见几何与深度；设备采样时间也不同。因此不把旧表与本表的差值宣称为代码性能优化或退化。本表作为修复后的性能基线，仍不替代 Release、温度稳定和长时间峰值分析。

## 原始证据与复现工具

便携证据位于 [_media/urp-realism/2026-10-11-9df1db9f](_media/urp-realism/2026-10-11-9df1db9f/production-sha256.txt)。
完整工作文件在两个 `D:/Games/work/crest-validation-*-20261011-90a93293/` 副本，按 `Retest-9df1db9f`、`Retest-9df1db9f-local-fix`、`Retest-9df1db9f-camera-fix` 和 `Retest-9df1db9f-camera-temporal` 区分。
Windows 连续帧原始包分别保存在 `Retest-9df1db9f-camera-temporal/WindowsCaptures-d3d11-recorder/game-frames.zip` 和 D3D12 同名目录；便携证据保存统计、相邻三帧图和完整日志。
已安装的最终设备与 Windows 录制工具源码另存 `tested-harness-android-final`、`tested-harness-windows-temporal`；其中 Windows 录制二进制在两种截图互斥保护加入前构建，实际通过的录制运行未传 `-crestScreenAudit`，设备最终包包含该保护及相对路径修正。
复现工具在 [UnityQualityTests](../tools/UnityQualityTests/README.md)，本地扩展源代码随本轮工作区改动提供。
这轮没有新的生产 Shader 改动，不宣称新画面的真实感提升，也没有完成 Release、热稳定、XR 或长时间内存峰值验收。
