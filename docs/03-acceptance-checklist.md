# DirMover 验收核对清单（架构师视角，给 QA 逐条打勾）

> **这份表的价值不在"再列一遍 §5"**，而在最后两列：**怎么验**（具体命令 / grep 模式 / 用例名）与**糊弄形态**（这条最可能被怎样假通过）。
> 对照文档：`docs/02-architecture.md` §5（T1–T14 验收条件）、§9.3（偏差记录 D-1 … D-12）、§9.4（**环境与产物前提**）。

## 使用说明（先读，否则会得出错误结论）

| 项 | 说明 |
|---|---|
| **dotnet 路径** | 本机 `C:\Program Files\dotnet\` 下**没有 `sdk\`**（只有 host/shared/Swix），`PATH` 上的 `dotnet --version` 会报 *No .NET SDKs were found*。一律用 `DN="$USERPROFILE/.dotnet/dotnet.exe"`（SDK **8.0.404**）。验证：`"$DN" --list-sdks` |
| **基线数字** | 当前套件 **Total 259 / Passed 259 / Failed 0 / Skipped 0**（2026-11-12 实测；QA 连跑三次 + team-lead 独立复跑，结果一致）。数字只用于"和基线比对"，**不要**把 `Skipped` 计入通过。 |
| **`Skip` 的口径** | 基线是 `skip=0`，所以**一旦你的报告里 `skip>0`，必须逐条点名并标"未验证"**。历史上那条 Skip 是**硬编码的缺陷记账**（不是环境限制）——见 T5-②，这是本轮最重要的反面教材。 |
| **固定盘数量（别沿用旧结论）** | 本机有 **C: / D: / E: 三个 `DriveType=3` 固定盘**（`Get-CimInstance Win32_LogicalDisk \| ? DriveType -eq 3`）。所以跨卷用例**不会**因 `drives.Count == 0` 提前返回。**任何"本机只有 C 盘"的说法都已作废**（§9.4）。 |
| **"单跑通过、全量失败"先别归因** | 这个现象**只能**推出"两次跑的不是同一份代码或同一份状态"，**不能**推出"测试之间相互干扰"。本仓库已有 `[assembly: CollectionBehavior(DisableTestParallelization = true)]`（`TestSupport.cs`），并行不是默认嫌疑。本轮那次假红先后被解释成"并行竞态"和"读盘快照"，两条都错——真相是单跑跑的是修好的版本。**基线前仍要求工程师停手**，但理由是"保证跑的是同一份代码"，不是"防止并发"。 |
| **"软通过"必须用 detailed 才看得见** | 用 `ITestOutputHelper` 打的"环境不具备"标记，**默认 verbosity 下 grep 命中 0 次**（实测），只有 `--logger "console;verbosity=detailed"` 才出现 1 次。所以"看输出里有没有那行"这条判据，**默认跑法永远失效**。判据统一写成：`dotnet test --filter "<用例>" --logger "console;verbosity=detailed"` → 输出里 grep 标记（命中 = 没真跑）。 |
| **引用写法：只写「文件:方法名」，不写行号** | 行号是易腐信息：本轮工程师只加了 XML 注释与一条标记，同一批用例的行号就漂了 3 处（`…DryRun…` 366→369、`…ScanReport…` 411→414、`…CrossVolume…` 456→469），而方法名在改名之前稳定。要定位就 `grep -rn "方法名" tests/`，成本远低于"按行号跳到别的方法上"。**若确需行号（给自动化用）**：冻结基线后重跑一次自检脚本刷新，否则一定会有人读到错位内容。 |
| **界面在本机无法验证（环境限制）** —— **已作废，留档不再使用**：真实根因是**产物缺框架 `resources.pri`**（`-p:EnableCoreMrtTooling=false` 把并入框架 pri 的那步也关掉了），csproj 补上 `StageFrameworkPri` 目标后 `publish\DirMover.App.exe` 实测 `ALIVE`，见 `02-architecture.md` §9.3 D-12 | **依据：两次"环境不行"的实验都不作数** —— ① `%TEMP%\winui-probe\` 的最小工程"也崩"，是因为那一版探针**没有"发布后补 `.xbf`"这一步**，产物里 `.xbf` 数为 0，崩因是缺编译后 XAML，证明不了"自包含不可用"；② 补 `.xbf` 之后出现的"框架依赖 `ALIVE` / 自包含 `CRASH`"分叉，同样被"**自包含 + 框架 pri → `ALIVE`**"推翻（A/B/C 三组对照见 §9.3 D-12）。⇒ 口径改成：**界面能不能跑取决于产物里那份 pri 在不在，不取决于机器**；界面项只剩「通过」「不通过」两档，**没有"运行未验证（环境限制）"这一档**。"桌面/DWM 正常"（`UserInteractive=True`、`SessionId=1`、`dwm`/`explorer` 同在 session 1）仍是有效旁证，但它否掉的只是"没有可交互会话"，与 pri 这件事无关 |
| **两类结论** | 「机器门禁」（grep / 计数 / 测试）必须**贴命令与输出**；「需手动」必须**写操作步骤 + 观察点**，不接受"界面正常"这类不可判定的句子。 |
| **App 侧边界** | 架构师按指令**未阅读** `src/DirMover.App/` 任何源码；App 侧只提供 grep 模式与构建/发布门禁，结论由 QA 自行判定。另：**`bin\` 下的 exe 不能作为界面证据** —— 它不是交付物，形态还随构建开关漂移（现 `bin` 与 `publish` 都已带框架 `resources.pri`，两份 `runtimeconfig.json` 同为 `includedFrameworks: Microsoft.NETCore.App 8.0.11`，即 .NET 都是自包含）；交付证据只认 `publish\`。**若哪天切回框架依赖形态**，本机机器级 `Microsoft.NETCore.App` 只有 10.0.15，框架依赖的 exe 会报 "You must install or update .NET"（§9.4）。 |

---

## 主表

| T# | 验收条件（一句话，可判定） | 怎么验 | 期望结果 | 糊弄形态 |
|---|---|---|---|---|
| T1-① | 解决方案恰好 3 个项目，不设 `DirMover.Interop` | `"$DN" sln list` | 3 行：`DirMover.Core` / `DirMover.App` / `DirMover.Core.Tests` | 建了 Interop 但没人引用；顺手查 `grep -c ProjectReference src/*/*.csproj` |
| T1-② | Core 零第三方、零 UI 依赖 | `grep -c PackageReference src/DirMover.Core/DirMover.Core.csproj`；`grep -rn "using Microsoft.UI\|using Windows\." src/DirMover.Core --include=*.cs`；`ls Directory.Packages.props Directory.Build.props` | `0` / 空 / 均不存在 | 只查 csproj，漏掉仓库根的中央包管理（`Directory.Packages.props`）把包"注"进 Core |
| T1-③ | 测试真的跑起来 | `"$DN" test tests/DirMover.Core.Tests/DirMover.Core.Tests.csproj -c Release` | `Failed: 0`、`Skipped: 0`，总数与基线（259）一致 | 用 Debug 跑、或只看"生成了 dll"；Release 才暴露 nullable 警告 |
| T2-① | 7 项目录真源与 PRD §2 逐字一致 | `UiTextTests` 中 `Assert.Equal(7, Catalog.All.Count)`，再逐项核 key / 显示名 / 默认路径 | 7，且 key 顺序与 PRD 表一致 | 只断言 `Count==7`，不看 key 拼写（`downloads` vs `Downloads`） |
| T2-② | `downloads` 是"权威值名 + GUID 别名"双份 | `grep -n "downloads" src/DirMover.Core/Catalog.cs` + 各一条用例 | 两处 GUID 齐全；权威值名 `{374DE290-…}`、别名 `Personal` | 只测权威名；别名写错/漏写也绿 |
| T2-③ | 文案只出自 `UiText`（**当前是"目标状态"，有 109 处存量**） | `grep -rnP "[\x{4e00}-\x{9fa5}]" src/DirMover.Core --include=*.cs \| grep -v UiText.cs` 再剔除纯注释行 → **预期 109**；明细见 §6.5 偏差表 | 新代码 0 新增；存量 109 处按"可接受 / 真违规 / 已声明例外"三类归档 | 直接 `grep -c` 报"有上千行中文"（把注释算进去），或宣称"只有 `UiText` 一处"——**这句现在是错的**，见 §6.5 |
| T2-④ | 状态列 5 分支 + 摘要省略规则 | `--filter "FullyQualifiedName~UiTextTests"`（约 28 条） | 全绿 | 只测主路径，漏"合并但未扫描"（`0 个文件 / 0 B`） |
| T2-⑤ | 列宽纯函数 4 条契约（和恒等 / 保护列不压缩 / 同输入同输出 / 极端宽度有解） | `--filter "FullyQualifiedName~ColumnFitTests"`（约 10 条） | 全绿，含 `Σ = available` | 只测常规宽度，不测 `available < Σ minimums` |
| T2-⑥ | 落点：可写→便携、只读→整体回落、PID 恒 `%LOCALAPPDATA%` | `--filter "FullyQualifiedName~AppPathsTests"`（约 5 条） | 全绿；`SingleInstancePidFile` 不随落点变化 | 只测可写目录，不测"去写权限后回落" |
| T3-① | `%VAR%…` 才 `REG_EXPAND_SZ`，判定用正则 | `grep -n "A-Za-z_" src/DirMover.Core/Registry/RegistryStore.cs`（应命中 `^%[A-Za-z_][A-Za-z0-9_()]*%(\\[^\0]*)?$`，约第 54 行） | 正则与 §3.2 **逐字一致** | 用 `StartsWith("%")` 近似；测试只喂 `%USERPROFILE%` |
| T3-② | 读取失败 ≠ 值不存在（三态） | `--filter "FullyQualifiedName~ReadFailureIsDistinguishableFromMissingValue"` | 绿 | 三态被压成两态（读失败当"缺失"） |
| T3-③ | `Unknown` 快照回滚跳过且**绝不删值** | 用例 `Rollback_UnknownEntriesAreSkippedAndNeverDeleted` **+ 人读** `RegistryStore.Rollback` 约 12 行 | `grep -c DeleteValue src/DirMover.Core/Registry/RegistryStore.cs` = **1**，且只在 `!entry.Existed` 分支 | 用例绿但 `DeleteValue` 被挪到 `Unknown` 判断之前 |
| T3-④ | 快照/意图日志的中文**可人工阅读**（D-9，**已修，可打勾**） | `grep -n "JavaScriptEncoder" src/DirMover.Core/Registry/RegistryStore.cs src/DirMover.Core/Engine/IntentJournal.cs`；或落盘后看 `backups\*.json` 是否出现 `\u` | 两处均命中 `UnsafeRelaxedJsonEscaping`（:60 / :99）；文件里直接看到中文路径 | 只断言"JSON 能反序列化"——`\uXXXX` 也合法，但违背"便于人工核对" |
| T4-① | 长路径统一走 `ToExtended`，不裸调 BCL | `grep -rn "Directory.Exists\|File.Exists" src/DirMover.Core --include=*.cs` | 裸调用只允许：`PathTools.cs`（实现体）、`DestinationGuard.cs` 盘符检查、`ShellIntegration.cs` PID、`AppLog.cs` 探针；其余必须带 `ToExtended(` | 只跑 `PathToolsTests` 的长路径用例，不扫调用点 |
| T4-② | 8.3 短名 / 尾点空格 / 保留名 / 非法字符 | `--filter "FullyQualifiedName~PathToolsTests"`（约 21 条） | 全绿 | 保留名只测 `CON`，不测 `CON.txt`（最常漏） |
| T4-③ | 同物理位置 / 包含关系 / 卷标识 | `--filter "FullyQualifiedName~DestinationGuardTests"`（约 24 条） | 全绿 | 用字符串 `StartsWith` 冒充"包含判断"、用盘符相等冒充"同卷" |
| T5-① | **红线 7 全绿（三向位置关系）**：源在目标内 / 目标在源内 / 同一物理位置 | `--filter "FullyQualifiedName~DestinationGuardTests"` 中这三类用例；`ResolvesToSame` 那条要能穿透 junction | 三类各至少 1 例；同一物理位置**穿透链接**判断，不是字符串比较 | 用 `StartsWith` 冒充"包含"、用盘符相等冒充"同卷/同位置"；一行里同时打勾"红线 7、8"（见「断言强度陷阱」第 6 类） |
| T5-①b | **红线 8 全绿（目标侧 9 条校验）** | `--filter "FullyQualifiedName~DestinationGuardTests\|FullyQualifiedName~DriveRootTargetGuardTests"` —— **盘根那条在 `DriveRootTargetGuardTests`（独立文件），只 filter 前者会误得"盘根无用例"**；再逐条对 §3.3 的 9 条 | 9 条各至少 1 例；且断言落到 `Reason` | 用单一 filter 就断言"9 条全覆盖"；或关键那条只测正向 |
| T5-② | **红线 8「盘根作目标 → 拒绝」** | 用例 `DriveRootTargetGuardTests.DriveRootAsTarget_IsRejected`；确认**没有 `Skip`**：`grep -n "Skip" tests/DirMover.Core.Tests/DriveRootTargetGuardTests.cs` | 逐条对：`ValidateDestination("C:\")` → `false`「盘根 C:\ 不能作为转移目标…」；`\\?\C:\` → `false`（**同一句文案**，理由不再是"非法字符"）；`\\?\C:\Windows` → `false`「不能把用户文件夹转移到系统目录 C:\Windows。」；**正例** `\\?\C:\UserData\me\dest` → `true`（合法长路径不被误拒） | **整轮最大的坑**：这条用例曾挂着 `[Fact(Skip="已知缺口…")]`，而文档与三方口径都把它解释成"本机缺第二个固定盘"——它实际只用 `Catalog.SystemDrive`，任何机器都能跑。一条被合理化的 Skip 把红线缺口藏了一整轮。另：**别拿 `C:\Users\...` 当正例**，它在 `ForbiddenTargetRoots()` 里，会被正确拒绝，写成正例会得到一个"看似 bug 其实正确"的红 |
| T5-③ | `\\?\` 形态不得"因非法字符"被误当成"禁区拦截成功" | 用例断言要**落到 `Reason`**；另喂 `C:\PROGRA~1`（不含 `?`）验证 8.3 短名绕不过去 | `Reason` 命中"禁区/系统目录"类文案，而非"非法字符" | 只断言 `Ok == false` —— `\\?\` 曾是被 `FirstIllegalCharacter(':')` 拒的，测试绿了但绿的不是它该绿的东西（D-6a） |
| T5-④ | 目标侧重解析祖先链，执行期再查一次；junction 用例**真跑** | `grep -rn "ReparseAncestor" src/DirMover.Core --include=*.cs` → 规划期 1 处 + 执行期 1 处；junction 四条用例用 `--logger "console;verbosity=detailed"` 跑并 grep `CanCreateJunction` 标记 | 两处调用点；本机 `cmd /c mklink /J` 可用（`TestEnv.CanCreateJunction == true`）→ `SamePhysicalLocationIsRejected` / `ResolvesToSame_DetectsJunctionPointingAtSource` / `ReparseAncestorFindsJunctionInAncestorChain` / `TargetUnderJunctionIsRejectedByPlanningGate` 真跑 | 只有规划期一处；**或**这些用例是 `if (!CanCreateJunction) return;` 形态，在禁用 junction 的机器上**静默变绿**（同 T7-④，必须 detailed + grep 标记） |
| T6/T7-① | 同名冲突：两份都在，`name (1).ext` | `--filter "FullyQualifiedName~FsOpsTests"` / `CopyVerifyTests` 冲突用例 | 目标出现 `name (1).ext`，`Conflicts` 非空 | 只断言"目标文件存在"，没断言第二份 |
| T7-② | 复用必须比内容（size+mtime 相同 ≠ 可复用） | 用例：构造 size+mtime 相同、内容不同的两文件 | 不复用 | 只比 size+mtime；或复用分支根本没被覆盖 |
| T7-③ | 取消后目标无 `.part` 残留 | `FsOpsTests` 取消用例 | 无 `*.part` | 用例只走"复制成功"路径 |
| T7-④ | **跨卷完整通道**（复制 + 7 维校验 + 空间闸门 + 删源） | ① `"$DN" test ... --filter "FullyQualifiedName~CrossVolume" --logger "console;verbosity=detailed"`；② 在这份输出里 grep「跨卷用例未执行」/`[ENV-MISSING]`：**命中 = 没真跑，未命中 = 真跑**（默认 verbosity 下那行根本不会出现，见使用说明）。用例：`SafetyRedLineEngineTests.RedLine1_CrossVolume_EndToEnd_KeepsDataAndRemovesSource` | 输出无标记；用例内部已 `Assert.NotEqual(VolumeKey(src), VolumeKey(dst))` 防退化，并断言 `ItemStatus.Ok` + `Stage=SourceRemoved`（含空目录） | ① 沿用"本机只有 C 盘、跨卷不可测"（**已推翻**，§9.4）；② 用默认 verbosity 判断"没有那行" —— **永远看不到，陷阱静默失效**；③ 只看绿点 |
| T8-① | 7 维校验 fail-closed | `CopyVerifyTests` 前 7 条 | 全部 `Verified=false`，`Detail` 含**维度名 + 样例** | 只测"干净树通过"；或只断言 `false` 不查 `Detail` |
| T8-② | 删源只有一个入口 | `grep -rn "Directory.Delete" src/DirMover.Core --include=*.cs` **且** `grep -rn "\.Delete(" src/DirMover.Core --include=*.cs`（抓 `DirectoryInfo.Delete` 实例形式） | `Directory.Delete` 恰好 1 处（`IO/FsOps.Verify.cs` 的 `SafeRemoveDirectory`）；无其它删目录/删文件的实例调用 | **只 grep 静态方法**，漏掉 `new DirectoryInfo(p).Delete(true)` |
| T8-③ | 只读文件可删；占用型错误退避后失败且提示重启 | `SafeRemoveDirectory_ClearsReadOnlyAndDeletes` / `SafeRemoveDirectory_LockedFile_FailsWithRestartHint` | 绿；文案含"重启本机后即可删除" | 退避 3 次被阉割成 1 次仍绿（用例没数次数） |
| T9-① | 空间未知 → 只该项失败、其余照常 | `SafetyRedLineEngineTests.RedLine6_UnknownFreeSpace_BlocksOnlyThatItem` | 该项 `Failed`，其余 `Ok` | 把 null 当 0 → 全项静默通过 |
| T9-② | 空间不足只阻断该项；同卷 rename 豁免空间闸门 | 空间不足用例 + 同卷 rename 用例 | 只有目标项 `Failed` | 只测"全部不足" |
| T10-① | 意图日志原子写、无 `.tmp` 残留 | `Begin_WritesValidJson_AndLeavesNoTempFile` | 绿 | 只校验 JSON 合法，不查 `.tmp` |
| T10-② | 半截 JSON **不得**被当"干净" | `ReadPending_HalfWrittenJson_ReportsErrorInsteadOfLookingClean` | `ReadError` 非空 | 返回空 `Items` 且 `ReadError` 也为空 |
| T10-③ | `pending` / `cancelled` 收尾一律注销 | `CloseRun_RemovesPendingAndCancelled`、`Finish_UnregistersItem_AndDeletesFileWhenLast` | 绿；`intent.json` 无 `cancelled` | 只验 `pending`；`cancelled` 残留会让下次启动误报并滚坏已成功项 |
| T10-④ | 未知阶段 / 旧格式当"损坏"，不当"沿用" | `ReadPending_UnknownStage_IsTreatedAsCorruption`、`ReadPending_LegacyEntryWithoutRegistry_IsMarkedUnknownAndNotTrimmable` | 绿 | 为"向后兼容"把未知阶段当 `Pending` |
| T10-⑤ | 备份目录不可写时**停用日志而不抛** | `Begin_WhenBackupDirectoryIsNotWritable_DisablesJournalWithoutThrowing` | 绿，后续写入是 no-op | catch 后继续写 → 每次操作都抛一次 |
| T11-① | 红线 1：复制或校验失败 → **不删源、不写注册表** | `ExecuteAsync_CopyFailure_KeepsSourceAndDoesNotWriteRegistry` | `Assert.Empty(env.Backend.Writes)` **且**源目录仍存在 | 只断言"源还在" |
| T11-② | 红线 5：写第二个值抛异常 → 本项已写值回滚、源保留 | `ExecuteAsync_RegistryWriteFailure_RollsBackAndKeepsSource` | 首值已回滚、`Stage=RegistryFailed`、源在 | 只断言 `Failed` 状态，不查"回滚真的发生了" |
| T11-③ | **`Skipped` 数必须报** | 跑测记录 `Skipped: N`；必要时 `--logger "console;verbosity=detailed"` 列出跳过项 | `Skipped: 0`（当前基线即 0）。若 >0 逐条点名并标"未验证" | 报"全绿"但不说 `Skipped`。**注意**：本机**不是**只有 C 盘，跨卷用例不会软跳过 —— 若你看到 skip，大概率是硬编码的缺陷记账（同 T5-②），必须查 |
| T11-④ | `dryRun` **零写入**：注册表 / 快照 / 意图日志 / `.part` / 日志文件全无副作用 | `SafetyRedLineEngineTests.RedLine10_DryRun_CreatesNeitherBackupNorLogDirectory`+ `ExecuteAsync_DryRun_HasNoSideEffects` | `Backend.Writes` 空；**传给引擎的 `backupDir`（一个运行前不存在的路径）未被创建**；沙箱内 `intent.json*` / `registry-*.json` / `*.part` / `*.log` 一律为空；源在、目标不存在。⚠ **不要断言 `IAppPaths.LogDirectory` 不存在** —— `TempAppPaths` 构造函数会预建 `logs\` 与 `backups\`，那样断言等于在测夹具 | **只查注册表没写**；或反过来把"断言 `logs\` 不存在"当成严格 —— 在现有夹具下它永远红/永远无意义，两种都验不到"引擎自己有没有建目录" |
| T11-⑤ | 取消后剩余项全 `Cancelled`、且不残留 | `ExecuteAsync_Cancelled_AllItemsCancelledAndNothingWritten` | 绿；`intent.json` 无 `cancelled` | 只查 `report.Cancelled` |
| T11-⑥ | 恢复路径 `registry_only`：源已不在时只改注册表 | `ExecuteAsync_RegistryOnly_WritesRegistryWithoutTouchingFiles`、`BuildRestorePlan_MissingSource_IsRegistryOnlyAndKeepsPercentForm` | 绿；写回保留 `%VAR%` 形态 | 文件被动过但没人查 mtime |
| T11-⑦ | 单项抛异常不中断整轮 | `ExecuteAsync_UnexpectedException_DoesNotStopRemainingItems` | 后续项仍被执行 | 只断言"没抛异常" |
| T11-⑧ | `--scan` 输出**四段**且告警内容真的渲染 | ① `grep -n "一、7 项\|二、可用\|三、注册表\|四、读取告警" src/DirMover.Core/Engine/ScanReport.cs`（第四段在 113 行）；② `SafetyRedLineEngineTests.RedLine10_ScanReport_RendersWarningsFromRealReadFailures`（，用真实读失败后端逼出告警）+ `ScanReportBuildAsync_ProducesResultAndWritesNothing` | 四段齐全；告警段渲染出「读取注册表失败」「枚举注册表失败」并带 `· ` 前缀；无告警时是单独一行「无」且**不出现** `· `；`Writes` 空且 `logs\` 不存在 | **只断言段标题存在、不断言告警内容** —— `Warnings` 为空时也会绿（`TransferEngineTests.ScanReport_HasThreeSections_AndWritesNothing` 就只有标题断言）。这类"标题绿"最容易被当成"这一段测过了" |
| T11-⑨ | `ScanReportResult.WriteToFile`：UTF-8 **带 BOM**、父目录按需建、失败返 `false` 不抛 | `ScanReportResult_WriteToFile_CreatesReadableFile` + 人读实现 | 首三字节 `EF BB BF`；不可写路径返 `false` | 只断言文件存在；或把异常吞成 `true` |
| T12-① | HKLM 严格只读（无写方法） | `grep -rn "WriteValue\|SetValue" src/DirMover.Core/Shell/`；`IRegistryBackend` 中 HKLM 仅读方法 | 空 | 写了写方法但"暂时没人调" |
| T12-② | 单实例：`Local\` 命名 + PID 文件 + 镜像名存活校验 | **需手动**，见「附录 · M1」 | 第二个实例静默退出 | 只读代码确认名字是 `Local\`，不做双开验证 |
| T12-③ | explorer 以**未提权**令牌重启 | **需手动**，见「附录 · M2」；配套 `--filter "FullyQualifiedName~ShellIntegrationTests"` | 新 explorer 的 Integrity = **Medium** | 只跑单测不真重启；或重启了但没看 IL |
| T14-① | App 构建 0 warning / 0 error（**属性个数：0** —— 干净副本实测零属性即可；"必须带 2 个开关"是旧结论，留痕见 §8-R6。**`build.cmd` 里传了 2 个 ≠ 本代需要 2 个**：那是给 1.5/22621 代留的保险，验收时按"裸跑是否 0/0"判，不按脚本传了几个判）——**由 App 负责人验证，架构师未构建 App；QA 已独立复跑** | ① **零属性**：`"$DN" build src/DirMover.App/DirMover.App.csproj -c Release`（QA 在**不含 `bin`/`obj` 的干净副本**上实测 **0/0，20.4 s**）；② 历史命令作对照：再加 `-p:Platform=x64 -p:EnableCoreMrtTooling=false --no-incremental`（实测同样 0/0）。**`MSB4062` 在当前包代不复现**：零属性、以及显式 `-p:EnableCoreMrtTooling=true` 都是 0/0 | `0 Warning(s) / 0 Error(s)`，且输出里有「框架 resources.pri 校验通过（1351648 B）」 | 用增量构建或 Debug；**在脏目录里得到 0/0 就当结论** —— 已有 `bin`/`obj`/上一代 `publish\` 会给出"看起来能编"的假绿，干净副本才是判据；或多加 `UseRidGraph` 等已作废开关"碰运气" |
| T14-② | 单屏零导航 | `grep -n "Frame\|NavigationView\|<Window" src/DirMover.App --include=*.xaml` | 只有 `App.xaml` / `MainWindow.xaml` 两个 Window | 写了 `Frame` 但注释掉 |
| T14-③ | App 层不碰注册表 / 文件系统 | `grep -rn "Registry\.\|Directory\.\|File\.\|new RegistryStore" src/DirMover.App --include=*.cs` | 只允许 `Path.Combine` 之类纯字符串运算（需人判） | 用 `System.IO.File` 全限定名绕开 grep |
| T14-④ | XAML 无中文字面量 | `grep -rnP "[\x{4e00}-\x{9fa5}]" src/DirMover.App --include=*.xaml` | 空 | 中文塞进 `x:Name`，或用 `&#x…;` 转义 |
| T14-⑤ | `build.cmd` 三子命令裸跑可用、无绝对路径、无 `NETSDK1198` | `grep -nE "[A-Za-z]:\\\\\|/c/" build.cmd`；裸跑 `build.cmd build` / `test` / `publish` | 0 命中；三者 exit 0；无 `NETSDK1198` | 把 SDK 版本写死成 `8.0.404`；或依赖 `PATH` 上的 `dotnet`（本机没有 SDK） |
| T14-⑥ | CLI 退出码 0 / 2 / 3 / 4 | **需手动**，见「附录 · M3」 | 四档与 §7.7 一致 | 只测 `--version` 一条就宣称"CLI 通过" |
| T14-⑦ | **`publish` 产物能真的启动**（**当前状态：已通过**）。**证据归属：①②③ 已由 QA 独立复跑（2026-11-12 23:06 / 23:12 两次，同一代产物 `exe 630,272 B @23:03:11`；**2026-11-13 08:32 又用当前源码 + 清空 `bin`/`obj` 重发一次并复跑，结论相同**），team-lead 另跑一轮，三方结论一致**。实测（QA 两次独立复跑）：`ALIVE`（t=6s）、`hwnd` 非 0（1,983,696 → 1,443,822，**每次都会不同，不得当一致性判据**）、标题逐字 `DirMover — 用户数据转移工具`（不是 `DirMover.App.exe`）、`wsMB` **约 160–180 MB**（QA 两次 161.9 / 162.2，team-lead 164 / **178.4**）、`threads` **57–58**、`logs\` 正常生成且 `binding-failed.log` 未出现。**这组数值只是"真空窗口 vs 空壳进程"的观察值，不是交付指标，不随文档更新同步**。**真实根因是产物缺框架 `resources.pri`**（A/B/C 三组对照见 `02-architecture.md` §9.3 D-12），`StageFrameworkPri` 从运行时 MSIX 取 1,351,648 B 那份补进产物后即正常；D-7 / D-12 的「环境限制」结论作废。**仍未验的是写操作主流程**（本机 7 项已在 `D:\UserData`，跑一遍等于空转，需一次性 VM） | **三条并列，按执行成本从低到高，① 失败不必跑 ②③**。**① 静态（离线可跑、无需交互桌面；仅自包含形态）**：`build.cmd publish` 必须打印「框架 resources.pri 校验通过（1351648 B）」，或直接 `$pri = Get-Item publish\resources.pri; $pri.Length -ge 1300000`（**两种取一即可，但优先用后一种**：构建日志里那条是 `MessageImportance.High`，`-v:q` 下同样不显示，靠日志判会随 verbosity 漂） —— **只 `Test-Path` 绝不算**（1,192 B 的应用空 pri 照样通过）；框架依赖形态本项 **N/A**（那份 pri 由已装运行时提供），仅作排障对照；**取数前提：`publish\` 必须是"刚 publish 完、未启动过、且没有构建在跑"的状态** —— 实测重建窗口内 `resources.pri` 会短暂不存在，静态门禁当场返回 `False`、几秒后恢复为 1,351,648 B（**假红**）；**若 `publish\` 刚被重建过，先等 `publish\DirMover.App.exe` 的 mtime 稳定再取数**（本轮那次假红就是踩在重建窗口里）；**② 存活（需交互桌面）**：`$p = Start-Process publish\DirMover.App.exe -PassThru; Start-Sleep 6; $p.Refresh(); if ($p.HasExited) { "CRASH " + $p.ExitCode } else { "ALIVE"; Stop-Process $p.Id -Force }`；**③ 若 CRASH**：`Get-WinEvent -LogName Application \| ? ProviderName -match "Application Error" \| select -First 1` 看出错模块 + 异常代码。附加观察（**只在 ② 的存活窗口内**）：`$p.Modules \| ? ModuleName -like 'Microsoft.UI.Xaml*' \| select ModuleName,FileName` → 判据是 **`other = 0`**（没有任何 WinUI 模块来自输出目录之外），**不是模块个数**。个数本身也分"数字 + 口径"：**narrow 口径**（`Microsoft.UI.Xaml*` + `Microsoft.WindowsAppRuntime*`）= **3**；**wide 口径**（`Microsoft.UI*` + `Microsoft.WindowsAppRuntime*` + `Microsoft.Graphics*`）= **8**（多出 `Microsoft.UI.Windowing.*` / `Microsoft.UI.Input.dll` / `Microsoft.UI.Composition.OSSupport.dll` / `Microsoft.UI.dll` 这 5 个不以 `Microsoft.UI.Xaml` 开头的）。**两个都对，差别在匹配模式，不随时点变**（team-lead 实测 t=3 / 10 / 16 s 完全相同；QA 用 narrow 在 t=6s 与 t=10s 都得到 3）；**空集 = `MODULES-UNREADABLE`，不算证据**（主进程 `requireAdministrator`，非提升读不到） | ① `publish\resources.pri` 存在且 **≥ 1,300,000 B**（实测 **1,351,648 B**）；② 存活 ≥ 6 秒**且** `MainWindowTitle` 逐字等于本程序标题（`DirMover — 用户数据转移工具`）**且** `logs\DirMover-yyyyMMdd.log` 被创建；**`hwnd` 非 0 不是判据**（apphost 错误框也有 `hwnd`）；③ 若退出，事件日志给出模块名与异常代码。**apphost「.NET 缺失」框的实测签名（QA 在干净副本上跑出来的一次真假活）**：进程**不退出**、`hwnd` **也非 0**（4,850,954）、`MainWindowTitle` = `DirMover.App.exe`、**27.4 MB / 8 线程**、**`logs\` 未生成** ⇒ 只用"存活"或"`hwnd` 非 0"判会假绿，真正的判别器是**标题逐字 + `logs\` 是否生成**。**强化证据（QA 独立实测，2026-11-12 23:07）**：全自包含产物跑出的 `--scan` 报告与旧**框架依赖**产物那份 `diff` **只差 2 行**（标题时间戳、`D:\` 可用空间 117.6→115.3 GB），其余逐字节一致 —— 即"能启动"之上还多了"可判定输出没变"；但**这两行本身天然不可复现**，拿 `diff` 当判据前必须先归一化，否则每次都是假红。**数量口径（若必须报数）**：顶层文件与递归文件**分开报**，并注明"是否已启动过 / 是否跑过 `--scan`"——启动 +1 条日志、`--scan` 再 +1 份报告；**且必须来自干净目录**：`build.cmd publish` **不清理** `publish\`，上一代遗留文件会混进清单 —— 实测仓库 `publish\` 里躺着一份 **2026-06-27** 的 `Microsoft.Web.WebView2.Core.winmd`（116,296 B），而干净 `dotnet publish --self-contained` 产物里根本没有它 ⇒ 干净自包含产物 = **顶层 333 / 递归 510**（未启动），仓库那份多出的 1 个就是它 | 甲、**跳过门禁 ① 直接记"红"并归因环境** —— 把构建缺陷当成环境限制（本轮就是这么误判了两轮）；乙、**用"产物齐全"代替"能跑"**：文件数 / 0 警告 / 任务管理器有进程，全都不算证据；丙、**以为补齐 4 个 `.xbf` 就够了** —— `.xbf` 与框架 pri 是**两件不同的事**（缺前者 `XamlParseException`、缺后者 `0xC000027B`），必须两条都验；丁、**用"存在"代替"身份"**：应用自建的空 pri 只有 **1,192 B**，`Test-Path publish\resources.pri` 照样通过、启动照样崩 —— 静态判据必须带**下限**（≥ 1,300,000 B），否则同名不同物一律假绿；戊、**拿 `runtimeconfig.json` 当形态判据**：它的 `includedFrameworks` 只反映 .NET、**不反映 WinUI** —— 框架依赖与自包含两份探针产物该文件 **md5 完全相同**（`59d61bde…`，QA 已复算），判形态只能靠产物字节 + 运行时模块加载路径；己、**在非提升的 PowerShell 里看到 `$p.Modules` 是空集就断言"不是自包含"** —— 主进程带 `requireAdministrator`，空集是权限问题，**空集不算证据** |

---

## 注意（两条必须写进报告的技术判断）

**注意 A —— `\\?\` 形态的拒绝理由必须断言到 `Reason`。**
`ValidateDestination` 现在**先剥 `\\?\` 前缀再判非法字符**（D-6a 已修），所以 `\\?\C:\Windows` 能走到禁区比较。但历史上它曾被 `FirstIllegalCharacter(':')` 抢先拒掉 —— 那时用例只断言 `Ok == false`，于是"绿得毫无意义"。
⇒ 今后凡涉及 `\\?\`、8.3 短名、尾点空格的用例，**断言要落到 `Reason`**（命中禁区类文案），而不只是 `Ok`。
**正例也要挑对**：验证"没被误拒"时用 `\\?\C:\UserData\me\dest`；`C:\Users\...` 在 `ForbiddenTargetRoots()` 里，写它会得到一个"看似 bug 其实正确"的红。

**注意 B —— `ScanReport` 在独立文件里。**
类型位置：`src/DirMover.Core/Engine/ScanReport.cs`（第四段 `四、读取告警` 在第 113 行），命名空间仍是 `DirMover.Core.Engine`；`TransferEngine.cs` 已降到 955 行。四段文案的 grep 目标指向**新文件**。

---

## 断言强度陷阱（比再加十条验收条件更值钱）

测试"绿了"有三种不同的含义，报告里请分开写：

| 陷阱形态 | 本轮实例 | 后果 | 对策 |
|---|---|---|---|
| **只断言 `Ok`，不断言 `Reason`** | 旧用例（已废弃）只查 `Ok == false`，实际是被"非法字符"拦，与系统目录无关；现拆成三条：`ExtendedPrefixSystemDirectory_IsRejectedForSystemDirectoryReason`（断言 `Reason` 含「系统目录」且 `DoesNotContain("非法字符")`）、`ExtendedPrefixDriveRoot_IsRejectedForDriveRootReason`、`ExtendedPrefixOrdinaryTarget_IsNotFalselyRejected`（`Ok=true` 不误拒） | 闸门走错分支也绿 | 断言落到 `Reason`/`Detail`/`Message` 上，并配一条**正例**防"一律拒绝" |
| **只断言"数量"或"存在"** | `Assert.Equal(Catalog.All.Count, result.Items.Count)` 只证条数 | key 拼错、显示名错也绿 | 断言具体集合（key 序列、文案全文） |
| **只断言单一写入面** | `dryRun` 只查 `Backend.Writes` | 快照/日志已落盘也绿 | 多维断言：注册表 + 文件系统两个目录 + 快照 |
| **用例名承诺 > 断言内容** | `ScanReport_HasThreeSections_AndWritesNothing` 名字说"全部段落"，实际四段里第四段没有 `Assert` | 少测一段而无人知 | 段标题 + 至少一段正文都断言，或改名 |
| **`Skip` 被当成环境限制** | `DriveRootAsTarget_IsRejected` 用 `[Fact(Skip="已知缺口…")]` 藏了一个红线缺口 | 套件全绿、缺口零覆盖 | 见到 `Skip` 先问"这台机器真的跑不了吗"，跑一遍再说。**替代写法**：用可 `grep` 的标记（如 `[ENV-MISSING]`）把"没跑"变成输出痕迹——**能被 grep 的跳过，才是可审计的跳过** |
| **一行验收塞多条规则** | 原 §5-T5① 把"红线 7 的三向位置关系"和"红线 8 的盘根/系统目录两套判定"塞进同一行 | 逐条打勾时**永远都能打满**，因为没人会拆开数 —— 这正是"盘根作目标"这一支能从验收表里漏掉的直接原因 | **一个验收行只装一条红线**；编号按简报 §10 固定不重排，但要拆成 `7-a / 7-b`、`8-a / 8-b` 这样的独立引用 |
| **断言对象其实是测试夹具** | 原 T11-④ 要求"`logs\` 与 `backups\` 不存在"，而 `TempAppPaths` 构造函数就已建好这两个目录 | 断言要么永远红、要么永远无意义 —— 验的是夹具，不是产品 | **一句话判据（QA 提，很准）："这条断言如果换成一个全内存的 `IAppPaths` 实现会怎样？"** 变成永真/永假 → 它验的是夹具。落地写法：断言**运行前不存在、只有产品会创建**的 `backupDir` + 扫沙箱内产物文件名 |
| **用"产物齐全"代替"可运行"**（以及同名的两个 `resources.pri`） | `publish` 里 4 个 `.xbf` 齐、0 警告、文件数对得上，**实测仍崩** `0xC000027B`；此后两个实验把"pri 路线"也否掉了 —— 但**被否掉的是"应用 pri"**（`makepri` 只索引自身资源，产物 1,192 B，补进去照样崩），**真正缺的是"框架 pri"**（运行时 MSIX 里那份 **1,351,648 B** 的资源索引，补进去即 `ALIVE`）。两个文件**同名、同扩展名，体积差约 1,100 倍**（1,351,648 ÷ 1,192 ≈ 1,134），这就是"同名文件 ≠ 同一物" | "看起来都齐了"是发布类缺陷最常见的遮羞布，而且会**把根因指错一整轮**：本轮先得出"pri 不是根因"（因为补的是**应用** pri），直到改测**框架** pri 才闭环 —— 一个"存在即通过"的判据（`Test-Path` 对 1,192 B 也成立）足够让否决结论建立在错对象上 | ① 只认**行为证据**：存活 ≥ 6 秒 / 事件日志给出模块名 + 异常代码（脚本见 T14-⑦）；② **静态判据必须带身份**：不写"文件存在"，写"存在 **且** ≥ 1,300,000 B"（分开 1,192 B 应用 pri 与 1,351,648 B 框架 pri）；③ 遵守"**结论可以删，否证过程不能删**"（§9.4 铁律 5）—— 但每次引用"已被否证"时，**必须回头核对否掉的到底是哪一个对象** |
| **把"配置文件里有这个字段"当成判据** | `runtimeconfig.json` 的 `includedFrameworks: Microsoft.NETCore.App 8.0.11` 看上去能判"是不是自包含"，但**框架依赖版与全自包含版该文件 md5 完全相同**（`59d61bde…`；QA 在 `%TEMP%\winui-probe-fd\out-mf` 与 `winui-probe-self` 两份探针上独立复算）—— 因为 `includedFrameworks` 只反映 **.NET**，WinUI 是否随包**根本不写进这个文件** | 用它对形态下结论 → 两种形态永远得出**同一个**结论，判据**恒真**、等于没有；更糟的是它会让人以为"这个我们已经验证过了" | 判据要选**能随被测属性变化**的量：形态 → `resources.pri` 的**字节数**（按名定位，≥ 1,300,000）或**运行时模块加载路径**（自包含落在输出目录内 / 框架依赖落在 `C:\Program Files\WindowsApps\Microsoft.WindowsAppRuntime.1.8_*`）。落地前**先问一句"这个量在两种情况下会不同吗"**，不同才配当判据 |
| **"存在"当判据（同名的 pri 有 4 个）** | `Test-Path publish\resources.pri` 对 **1,192 B** 的应用空 pri 同样通过；而 `publish\` 根下同时躺着 4 个 pri（QA 逐个实测）：`resources.pri` **1,351,648**、`Microsoft.UI.Xaml.Controls.pri` **1,258,296**、`Microsoft.UI.pri` 101,528、`Microsoft.WindowsAppRuntime.pri` 8,648。其中 `Controls.pri` 距 1,300,000 的阈值**只差 41,704 B** | "存在即通过"→ 假绿；"取最大的 pri"或"存在任意 ≥1.3M 的 pri"→ **今天恰好对、换包就假绿**（`Controls.pri` 一长过阈值，判据就再也分不清谁是谁） | 判据必须**按文件名定位 + 比尺寸**：`$p = Get-Item publish\resources.pri; $p.Length -ge 1300000`。**不要**写"存在某个 ≥1.3M 的 pri"，也不要写"取最大的那个" |
| **只给数字、不给口径**（计数类判据） | 本轮**三次**同型：① 文件数 `333 / 424 / 513` —— **顶层文件 / 顶层条目 / 递归文件** 是三种算法（同一份 `publish\`：333 文件 + 91 目录 = 424 条目；**真干净未启动态是 `333 / 422 / 510`**，差的是 2 个运行时目录（`logs\`、`backups\`）+ 3 个文件（1 条日志 + 2 份 scan 报告））；**`334 / 425 / 513` 是脏 `bin`/`obj` 未清时的旧数**（多一个 `Microsoft.Web.WebView2.Core.winmd`，1.5 代包遗留），**已作废**；② 两份 `--scan` 报告被说成"逐字相同"，实测 **23 行里 2 处不同（21 行一致**，差时间戳与 `D:\` 可用空间，QA 用 `diff \| grep -c "^[<>]"` = 4 即 2 处替换核过）；③ 模块数 `narrow=3 / wide=8` —— 同一进程 t=3/10/16 s 都不变，差别只在**过滤模式** | 下一个人看到两个不同的数，**无法判断是"矛盾"还是"两种算法"**：要么把口径差误判成缺陷、要么把真缺陷当口径差放过。本轮前两次翻车（"本机只有 C 盘""自包含不可用"）都起于这个形状 | 计数类判据必须写成"**数字 + 口径 + 时点**"三件套（例：`递归文件 512（已启动过一次 `--scan`，含 1 条日志）`）；跨人比对前**先对齐口径，再解释差异** |
| **用增量构建冒充"干净构建"** | `dotnet publish -o 新目录` 看着像干净产物，但 `bin`/`obj` 是脏的：`obj\…\DirMover.App.csproj.FileListAbsolute.txt` 里仍列着**上一代（1.5 包）**留下的 `Microsoft.Web.WebView2.Core.winmd`（116,296 B / 2024-06-27），publish 会照单搬走 → 新目录里照样出现它（实测顶层 **334**、递归 **511**） | 本轮因此**反向误判**过一次：看到"新发布也带这个文件"，就断定它是合法产物、把已经清掉的它又想加回去。真相是 1.8 包**零 winmd**、运行时 MSIX 内也没有、全缓存中该文件只存在于 `microsoft.windowsappsdk/1.5.240627000` ⇒ 它是旧代残渣。**`-o` 换目录 ≠ 干净**，`FileListAbsolute` 会继承历史 | 干净 = **移走/删除 `bin` 与 `obj` 后重建**（实测：`333 / 422 / 510`，且该 winmd 不再出现）。判定"某文件是不是遗留"**不能看 mtime**（MSBuild 复制保留源时间戳，1.8 包内的 2026-09 文件也一样"很旧"），只能看**清空中间产物后是否还产出** |
| **"进程没退出"当判据（假活 / 空壳进程）** | 零属性 `publish` 的产物启动后**不退出、`hwnd` 也非 0**（QA 实测 **4,850,954**），但它只是 apphost 的「.NET 缺失」对话框：`title=[DirMover.App.exe]`、**27.4 MB / 8 线程**、**`logs\` 未生成**；真身是 `title=[DirMover — 用户数据转移工具]`、**约 161–178 MB / 57–58 线程** | `HasExited=false` 与"`hwnd` 非 0"**都会让它通过**，而它一个功能都没跑 —— 这是"看起来活了"的最短路径，比崩溃更危险（崩溃至少会报错） | 用**不变量组合**判：标题**逐字**等于 `WindowTitle` + 线程数量级（57 vs 8）+ **`logs\` 是否生成**。其中标题与 `logs\` 最硬（与机器性能无关）；**`hwnd` 非 0 单独不成立**（假活样本也是非 0） |

---

## 附录 · 需手动项（写清步骤与观察点）

| # | 项 | 步骤 | 观察点（可判定） |
|---|---|---|---|
| **M1** | 单实例 | ① 管理员启动 `DirMover.App.exe`；② 再启动第二个；③ 看 `%LOCALAPPDATA%\DirMover\single_instance.pid`；④ 杀掉第一个再启动 | 第二个**静默退出**（无新窗口、无弹窗）；PID 内容 = 第一个 pid；杀进程后能重新获锁并开窗 |
| **M2** | explorer 未提权重启 | ① 管理员启动 app；② 点「重启资源管理器」；③ 用 Process Explorer 看新 explorer 的 Integrity | Integrity = **Medium**（不是 High）；桌面/任务栏恢复；状态栏「重启中…」后回到就绪 |
| **M3** | CLI 退出码 | 逐条跑 `--scan` / `--version` / `--help` / `--dry-run` / 指向不存在盘符的目标 | `%ERRORLEVEL%` = 0/0/0/0/2；`--scan` 生成 `logs\scan-*.txt` 并弹**只读文本弹窗**；`--dry-run` 有「试运行模式」徽标且**无**提权重启入口 |
| **M4** | 真实迁移冒烟 | 以 **D: 或 E:** 作目标：勾 1–2 项 → 转移 → 资源管理器核对 → 「恢复默认值」 → 再核对 | 目标有数据且源已删；注册表指向目标；恢复后回到 `C:\Users\<me>\…`；`backups\` 有快照；`logs\` 无 Error；`intent.json` 里中文**直接可读**（D-9） |

---

## 与文档的对应

| 本表 | `docs/02-architecture.md` |
|---|---|
| T1–T12 | §5 T1–T12 的验收列 |
| T14 | §5 T14 + §8-R1（构建/发布门禁） |
| T5-① / T5-①b | §5-T5 ①-a / ①-b（红线 7、红线 8 分开引用，编号按简报 §10 不重排） |
| T5-② | §9.3 **D-6**（盘根作目标：已修复并闭环）、§9.4 教训表 |
| T5-③ / 注意 A | §9.3 **D-6a**（`\\?\` 被非法字符抢先拒：已修，断言口径保留） |
| T7-④ | §9.4（本机 D:/E: 均为固定盘；跨卷定性收窄） |
| T3-④ | §9.3 **D-9**（JSON 中文转义，QA 编号 D-4） |
| T11-⑧ | §9.3 **D-1**（`--scan` 单份实现、四段） |
| T14-⑦ | §9.3 **D-7**（`publish` 启动即崩 → 已通过，闭环标准两条）+ **D-11 / D-12**（根因是缺**框架** pri；被否掉的是**应用** pri）+ 「D-7 的排除表」、§8-R1 发布门禁 ①②③（静态 pri / 存活 / 事件日志） |
| 断言强度陷阱 | §9.4「两条同源教训」 |
| 12 条红线各自覆盖强度（强/弱/无）与"本轮新增"用例 | `docs/04-redline-coverage.md`（QA 产出，与本表互为索引） |
