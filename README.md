# DirMover（C# 重写版：WinUI 3 + WPF 双外壳）

把 Windows 的 7 个用户文件夹（桌面 / 文档 / 下载 / 图片 / 音乐 / 视频 / 保存的游戏）从系统盘迁到其它盘，并同步注册表让系统认新位置。

本仓库是 [Sword6988/dirmover](https://github.com/Sword6988/dirmover)（Python + tkinter，v3.0.1，MIT）的 **C# 全面重写**。重写目标是**行为对等**，不是界面相似：同样的判定顺序、同样的拒绝条件、同样的失败文案、同样的"校验通过才删源"。

同一套引擎、同一套视图模型，两个界面外壳：**WinUI 3**（`build.cmd portable`，全自包含便携包）与 **WPF / .NET Framework 4.8**（`build.cmd wpf`，单文件 exe）。见「一个引擎，两个外壳」。

原始项目源码留在 `repo-src/` 供对照，研究结论见 `docs/00-research-brief.md`。

---

## 为什么是"行为对等"而不是"功能相似"

这个工具会**删掉用户的源目录**。它唯一的价值是"搬完之后数据还在"。所以：

- 一切可判定的逻辑（路径规范化、目标校验、校验算法、事务阶段、文案、列宽）都压在 `DirMover.Core`，不依赖 UI、可单测；
- 界面层只做绑定与转交，不参与任何安全判定；
- 有一条**全仓唯一的删除入口**（`FsOps.SafeRemoveDirectory`），它的调用点只有一个，且在两道闸门之后。

这三点是硬约束，改了就不算这个项目。文档 §6「跨文件约定」与 `docs/03-acceptance-checklist.md` 记录了具体判据。

---

## 快速开始

**环境**：Windows 10 1809+ / .NET 8 SDK（**仅构建期**需要）。**目标机零前置依赖**：产物为全自包含（.NET 8 + WinUI 1.8 都在包内），**不需预装 .NET，也不需预装 Windows App Runtime**（见「已知偏差 4」）。

> 本机 `PATH` 上的 `dotnet` 可能没有 SDK。`build.cmd` 会自动按 `DOTNET_ROOT` → `%USERPROFILE%\.dotnet` → `PATH` 探测，探测不到会打印指引。

```bat
build.cmd build       :: 编译 Core + App
build.cmd test        :: 跑全量单元测试
build.cmd publish     :: 发布到 publish\（全自包含 .NET + WinUI，含框架 resources.pri）
build.cmd portable    :: 打便携包到 dist\（可运行目录 + zip，即 Release 附件）
build.cmd wpf         :: 打 WPF/.NET FW 4.8 单文件包到 dist\（一个 exe + zip）
```

**便携版（交付形态）**：`build.cmd portable` 产出一份**免安装**包——`dist\DirMover-<版本>-portable-win-x64\` 与同名 `.zip`。解压到任意位置（含 U 盘）双击 `DirMover.App.exe` 即可运行：不需要 .NET 运行时，不需要 Windows App Runtime，不写用户配置（目标盘与勾选是纯内存态）；程序目录可写时日志与备份落在同目录 `logs\`/`backups\`（便携模式），只读位置则回落 `%LOCALAPPDATA%\DirMover`。包内附 `README-Portable.txt` 使用说明。**单文件版本已废弃**——它每次启动把原生 dll 自解压到 `%TEMP%`，与"便携"正好相反。

**WPF 单文件版（兼容形态）**：`build.cmd wpf` 产出 `dist\DirMover-<版本>-wpf-net48\DirMover.exe` 与同名 `.zip`——**一个 exe**（Costura 把 Core 与依赖织入，无 exe.config）。面向"有 .NET Framework 4.8 运行时（Win10 1809+ 自带）但没有 Windows App Runtime"的机器。引擎与视图模型与 WinUI 版是**同一份源码**（`DirMover.Core` 多目标 net8/net48，ViewModel 链接编译），两版行为一致由编译器保证；界面层是 WPF 重绘，视觉与 WinUI 版同源令牌。构建只需 .NET 8 SDK（引用程序集由 `Microsoft.NETFramework.ReferenceAssemblies` 包提供，目标机不需要任何预装）。

直接调 `dotnet` 时（**本代包 `Microsoft.WindowsAppSDK 1.8.260921001` + `BuildTools 10.0.26100.4654` 实测**）：`build` **零额外属性**即可 0 警告 / 0 错误；`publish` 必须带 **`--self-contained`**，否则产出的是 .NET 框架依赖产物，启动是**假活**（`title=[DirMover.App.exe] wsMB=27.4 threads=8` —— apphost 的 .NET 缺失框，不是程序起来了）：

```bat
dotnet build   src\DirMover.App\DirMover.App.csproj -c Release
dotnet publish src\DirMover.App\DirMover.App.csproj -c Release --self-contained -o publish
```

`build.cmd` 里仍然传的 `-p:Platform=x64 -p:EnableCoreMrtTooling=false` 是**给旧包版本（1.5 / 22621 代）留的保险**（那一代那里会撞 `AnyCPU is not supported for Self Contained mode` 与 `MSB4062`），本代已非必需 —— **"脚本里传了 2 个"与"本代需要 2 个"是两件事**。`-p:UseRidGraph=true` **不需要**：csproj 已显式指定 `RuntimeIdentifier=win-x64` 与 `Platforms=x64`，推断 RID 图的前提不成立。

**运行**：`DirMover.App.exe` 清单声明 `requireAdministrator`，启动即请求提权（写 HKCU 的 shell folders 与删源都需要）。

```
DirMover.App.exe              正常界面
DirMover.App.exe --dry-run    试运行：完整规划与判定，零副作用，状态栏显示徽标
DirMover.App.exe --scan       只读扫描：写 logs\scan-yyyyMMdd-HHmmss.txt + 只读弹窗展示，不新增页面
DirMover.App.exe --version    版本与参数说明
```

退出码：`0` 正常 / `2` 环境不满足（无可用目标盘、未知参数）/ `3` 窗口或 `XamlRoot` 创建失败 / `4` 未预期错误。

---

## 项目结构

```
DirMover.sln
build.cmd                     构建/测试/发布/便携/WPF 五命令 + SDK 探测（ASCII-only + CRLF，cmd.exe 代码页限制）
src/
  DirMover.Core/              业务与安全判定，零第三方依赖、零 UI 依赖（21 文件 / 9115 行）
    Compat/                     net48 兼容层（见「一个引擎，两个外壳」）：Compat.cs 跨目标统一出口、
                                Net48Shims.cs 命名空间遮蔽（ThrowIfNull / HashCode）、Net48Polyfills.cs 语言特性垫片
    Catalog.cs                  7 项目录真源、注册表子键与 GUID、空间余量、禁区根（按 SystemDrive 现算）
    RunConfig.cs                运行配置（纯内存态），目标路径合成
    UiText.cs                   文案出口（详见「已知偏差」）
    Layout/ColumnFitCalculator  列宽算法纯函数（最大余数法）
    Diagnostics/AppLog.cs       日志落点解析与文件日志（唯一 using 只有 BCL）
    Registry/                   后端抽象 + 快照/回滚/REG_EXPAND_SZ 判定
    IO/PathTools.cs             长路径、8.3、subst、ResolvesToSame、HumanSize、固定盘枚举
    IO/DestinationGuard.cs      9 条目标校验 + 源侧风险判定（盘根 / 系统目录 / 链接祖先）
    IO/FsOps.Scan.cs            全树遍历、规模统计、文件指纹
    IO/FsOps.Copy.cs            复制合并（1 MiB 分块、边读边算摘要、稀疏、硬链接、.part 原子改名）
    IO/FsOps.Verify.cs          7 维校验、原子改名、**唯一删除入口 SafeRemoveDirectory**
    Engine/PlanData.cs          11 个阶段、5 种结论、计划项与报告、进度节流
    Engine/TransferEngine.cs    编排：BuildPlan / ExecuteAsync / RestoreDefaultsAsync + 单项事务
    Engine/ScanReport.cs        --scan 四段只读报告（采集与排版分离）
    Engine/IntentJournal.cs     意图日志（原子写、损坏可感知、阶段推进、快照裁剪）
    Interop/NativeMethods.cs    Win32 调用（卷信息、长路径、硬链接、稀疏、提权拉起）
    Shell/ShellIntegration.cs   提权重启、explorer 重启（三态判定 + 未提权拉起）、单实例
  DirMover.App/               WinUI 3 界面（8 个 .cs / 2863 行 + 4 个 .xaml / 525 行）
    App.xaml(.cs)               单实例前置、CLI 解析与退出码、全局异常兜底、绑定失败日志
    MainWindow.xaml(.cs)        单屏 7 行布局、关窗收尾（200 ms 轮询 / 60 s 上限）
    Dialogs/DialogService.cs    14 类业务弹窗 + 只读文本弹窗的唯一出口（串行化、XamlRoot 必填）
    ViewModels/                 MainViewModel（状态机与命令）、FolderRowViewModel、ObservableBase
    Views/TableColumnLayout.cs  列宽布局（40 ms 防抖 + 高水位 + 每次返回新 GridLength）
    Controls/RowStateConverters 7 种行状态 → 画刷（零颜色字面量，全部走 ThemeResource）
    Theme/Tokens.xaml           颜色与间距令牌（唯一允许出现色值的地方）
  DirMover.Wpf/               WPF / .NET FW 4.8 单文件界面（8 个 .cs / 1610 行 + 5 个 .xaml / 1089 行）
    App.xaml(.cs)               宿主：单实例、CLI 与退出码、全局异常兜底、ContentRendered 收尾
    MainWindow.xaml(.cs)        7 行布局 1:1 重绘；FormattedText 量列宽；Closing 先取消再等待
    Compat/WinUiThreadShim.cs   在 Microsoft.UI.Dispatching 下用 WPF Dispatcher 实现 DispatcherQueue
    ViewModels/                 链接编译自 DirMover.App（同一份 MainViewModel，零改动）
    Views/TableColumnLayout.cs  列宽布局（与 WinUI 版同一算法，仅 GridLength 换类型）
    Controls/RowStateConverters 7 种行状态 → 画刷（资源键与 WinUI 版逐项同源）
    Dialogs/                    自绘无边框弹窗 + WpfDialogService（14 类弹窗，SizeToContent 自适应）
    Theme/                      Tokens.xaml 浅色档全量、Styles.xaml 用 ControlTemplate.Triggers 重做交互态
tests/
  DirMover.Core.Tests/        16 文件 / 5072 行 / 268 条（xunit 2.9.2，net8 侧）
tools/
  check-bindings.py           XAML 绑定路径静态校验（build.cmd wpf 第一步自动跑；
                              WPF 绑定写错运行期不报错也不抛异常，没有 app.config 就没有运行期诊断）
docs/                         研究简报、PRD、架构、验收清单、类图、时序图
```

四个工程，不设 `DirMover.Interop`：P/Invoke 属于 Core 的 `Interop/`，因为它只服务于 Core 的判定，单独成包只会让调用点分散。`DirMover.App` 与 `DirMover.Wpf` 是同一引擎的两个外壳，彼此不引用、互不感知。

---

## 一个引擎，两个外壳（WinUI 3 / WPF net48）

这个工具会删源目录，所以**两版之间不允许出现"行为差异"**。而"两版行为一致"如果靠人工同步，迟早会漏。这里的做法是把它变成**编译期约束**：

| 层 | WinUI 3 版 | WPF 版 | 共用方式 |
|---|---|---|---|
| 引擎 `DirMover.Core` | `net8.0-windows` | `net48` | **同一份源码，多目标编译** |
| 视图模型 `ViewModels/` | 编译 | **链接编译**（`<Compile Include="..\DirMover.App\ViewModels\*.cs" Link="..."/>`） | 同一份源码，零复制 |
| 界面层 | XAML + WinUI 控件 | XAML + WPF 控件 | 各自重绘，视觉令牌同源 |

视图模型这层是关键的：`MainViewModel`（1372 行）里有全部状态机、命令可用性、进度映射、关窗收尾。它能被 WPF 直接编译，靠的是它**只依赖 UI 框架两处**——`Microsoft.UI.Dispatching.DispatcherQueue` 和 `TableColumnLayout`。前者在 `Compat/WinUiThreadShim.cs` 里用 WPF `Dispatcher` / `DispatcherTimer` 提供了**同命名空间的同形实现**，后者在 WPF 工程里按同一算法重写（只把 `GridLength` 换了类型）。于是那个 1372 行的文件改一个字，两版同时生效。

**net48 兼容层为什么不散成 `#if`**：`Compat.cs` 是跨目标统一出口，`#if` 只出现在这一个文件里。理由不是"好看"，是**可证伪**——"哪 40 处 API 在 net48 上不存在"这件事，看一个文件就够了；散到 9 个调用点之后，下次谁再加一处 `Environment.ProcessId` 要靠编译器撞墙才发现。

两个细节值得记下来，因为都不是"顺手能想到"的：

- **`ArgumentNullException.ThrowIfNull` / `HashCode.Combine` 是命名空间遮蔽，不是扩展方法**。在 `DirMover.Core` 命名空间里声明同名类型（含 `CallerArgumentExpression` 参数），调用点 `ArgumentNullException.ThrowIfNull(x)` 一个字都不用改——因为 C# 名称查找优先取**更内层命名空间**里的类型而不是 `System` 里的。这把"~20 处调用点"的改动量变成了 0。
- **重解析点没走 `LinkTarget` / `ResolveLinkTarget`**（net48 没有），而是用 `CreateFileW(FILE_FLAG_OPEN_REPARSE_POINT)` + `DeviceIoControl(FSCTL_GET_REPARSE_POINT)` 手工解析 `REPARSE_DATA_BUFFER`，剥掉 `\??\` 前缀。这不是造轮子炫技：复制阶段要**重建**源里的链接（包括悬空链接），"读不到目标就跳过"会让符号链接静默变成空目录，而 7 维校验里的"链接齐备"那维正好会因此失败——宁可写 80 行 interop，也不要一个会误判的环境。

**单文件的承诺是可验证的**，不是靠 `PublishSingleFile` 那几个开关：`build.cmd wpf` 在 publish 之后会遍历输出目录，**除 `DirMover.exe` 外多出任何一个文件就 `goto :fail`**。这条守卫存在的理由是踩过：MSBuild 默认会生成 `DirMover.exe.config`（绑定重定向 + `supportedRuntime`），一旦出现，"一个 exe"就成了"exe + 配置"两件。关掉 `AutoGenerateBindingRedirects` / `GenerateBindingRedirectsOutputType` / `GenerateSupportedRuntime` 三个开关才能干净——代价是放弃自动绑定重定向，而这个代价可以用实测消掉：Costura 织入的程序集由它自己的 `AssemblyResolve` **按简单名**解析、忽略版本，所以 `System.Text.Json` 全链路在无 config 下能跑通（验证方式：一次性探针工程，用真 `DirMover.Core` 跑一次 `JsonSerializer` 往返，删掉生成的 `.config` 后仍输出 `OK name=测试 count=3`；该探针是临时验证物，未入库）。

代价如实写在这里：WPF 版是**界面层重写**（8 个 `.cs` / 1610 行 + 5 个 `.xaml` / 1089 行），不是"改个 target 就完事"。WinUI 3 只跑 .NET 6+ 且依赖 Windows App Runtime，与"一张 exe 丢进任何 Win10 机器"的运行时约束互斥，只能各建宿主。

---

## 关键模块划分

**Core 按"职责能不能被单独证伪"切，不按技术分层切。**

| 域 | 负责 | 不负责 | 关键不变量 |
|---|---|---|---|
| `Catalog` / `RunConfig` | 真源数据、目标路径合成 | 判定 | 禁区根 **每次现算**（系统盘可被注入，测试靠这条验证"随盘变化"） |
| `PathTools` | 路径规范化、长路径、物理同一性 | 业务规则 | 所有存在性判断走 `PathTools.Exists`，裸 `Directory.Exists` 只在 4 处固定短路径 |
| `DestinationGuard` | 目标 9 条 + 源侧风险 | 执行 | 先判盘根，再判禁区子树，最后判 8.3 与链接祖先；每种形态逐个判，任一命中即拒（fail-closed） |
| `FsOps.*` | 扫描、复制、校验、删除 | 编排、注册表 | 删除只有 `SafeRemoveDirectory` 一个入口；复用必须比内容不能只比 size+mtime |
| `Registry*` | 读、写、快照、回滚 | 判定时机 | 读失败 ≠ 值不存在（三态）；`Unknown` 条目回滚时跳过且**绝不删值** |
| `Engine` | 规划、单项事务、注册表同步、意图日志 | 界面 | 删源前两道闸门：`ResolvesToSame` + `DescribeSourceChange`；任一不过就保留源 |
| `Shell` / `Diagnostics` | 系统集成、日志落点 | 业务 | `Diagnostics` 只依赖 BCL（启动期崩溃兜底不能被引擎拖进加载图） |

**App 只做四件事**：把 `PlanItem` 映射成行、把用户操作转成命令、把进度转成界面、把关窗转成可等待的收尾。安全判定一行都不在 App 里（`grep` 可验：App 不 `new RegistryStore`、不直接调 `FsOps`）。

---

## 实现要点

1. **事务顺序固定**：规划 → 复制（含空目录、链接重建）→ 源↔目标全量校验 → 写注册表 → 校验通过才删源。任何一步失败就停在该步，源原样保留。
2. **摘要属于源，不属于目标**：边读源边算 SHA-256（1 MiB 分块），避免"把目标读一遍发现和目标自己一致"这类自证。
3. **7 维校验**：文件集合差集、大小、摘要、空目录、链接齐备、`skipNames` fail-closed、源侧未变。任一维不过 → `Verified=false` 且 `Detail` 带维度名与样例。
4. **同名冲突保留两份**：目标已有同名不同内容 → 落成 `name (1).ext`，并记录在 `Conflicts`；校验按**实际落点**比对，否则这条路径永远走不完。
5. **路径 A / 路径 B**：同卷且目标不存在 → 原子 rename（零字节需求、不进空间闸门）；否则走复制通道。跨卷时 `Directory.Move` 由 `VolumeKey` 先判、失败即回退。
6. **长路径与路径歧义**：≥240 字符走 `\\?\` / `\\?\UNC\`；8.3 短名还原后再比较；`subst` 穿透；"同一物理位置"用卷序列号+文件索引而不是字符串。
7. **注册表双形态同步**：权威值名 + GUID 别名同时写；`User Shell Folders` 必写，`Shell Folders` 只写已存在的值；`REG_EXPAND_SZ` **仅当值以 `%VAR%` 开头**（中间含 `%` 仍是 `REG_SZ`）。
8. **快照与回滚**：写之前抓快照，失败就地回滚本项；快照里"读失败"的条目记 `Unknown`，回滚时跳过、绝不 `DeleteValue`。回滚后裁剪快照，避免下次启动误用旧值。
9. **意图日志**：`backups\intent.json` 记录每个文件夹的 11 个阶段，`.tmp` + `File.Replace` 原子写；半截 JSON 必须报"损坏"而不是当成"干净"（否则下次启动会把未完成项当已完成）。
10. **空间闸门**：`needed × 1.05 + 64 MiB`；`FreeSpace == null` 即阻断该项（读不到容量不等于装得下）；同卷 rename 豁免；不足只阻断该项，其余照常。
11. **重解析点三处闸**：规划期判、执行期建目录前再判、复制内部再判（含祖先链）。威胁模型是"注册表被改成指向系统目录的 junction"，纯字符串比较看不出来。
12. **取消是协作式的**：`CancellationToken` 贯穿 4 处检查点；取消后剩余项全 `Cancelled`，`intent.json` 不残留 `cancelled`，目标侧不留 `.part`。
13. **线程模型**：Core 不认识 `DispatcherQueue`。`IProgress<T>` 在 UI 线程构造 → 回调天然回 UI；节流（≥100 ms）放在 Core，UI 只负责画。
14. **列宽是纯函数**：`Fit(needs, minimums, quotas, available)` 最大余数法，各列之和**恒等于**可用宽；保护列（名称/状态）不压缩；同输入同输出；`GridLength` 每次返回新实例（struct 复用不触发刷新）。
15. **单实例**：`Local\DirMover_Single` 命名互斥体 + PID 文件 + 镜像名存活校验。两个进程同时删源是与红线同级的风险。
16. **explorer 重启要查 `AutoRestartShell`**：HKLM `Winlogon` 三态（1 或不存在 → 允许，0 → 跳过，读失败 → fail-closed 跳过）；拉回 explorer 用**未提权令牌**，否则资源管理器会长期以高完整性运行。

---

## 与原项目的差异

**只有三处，且都是有意的。**

| 项 | 原项目 | 本重写 | 原因 |
|---|---|---|---|
| 内容摘要 | blake2b-128 | SHA-256（1 MiB 分块） | .NET 无内置 blake2b；SHA-256 在同量级性能下碰撞概率更低。**摘要用途不变**：只用于"复制后比对"，不参与任何持久化标识 |
| 文件属性位 | 保留 ReadOnly / Hidden | 只保留稀疏位，不保留只读/隐藏 | ①7 维校验不含属性位，不会出现"校验通过但内容不同"；②保留只读会让删源与后续清理更容易失败，而删源失败会把已完成项降级成 `SourceRemovalFailed`；③稀疏位影响实际磁盘占用与复制路径，必须保留 |
| 界面形态 | tkinter 单窗 | WinUI 3 单窗 7 行布局 | 同一屏、同一信息密度、零导航；按钮形态、状态列 5 分支、文案逐条对齐 |

其余（判定顺序、拒绝条件、文案、空间规则、注册表形态、恢复默认值、单实例、explorer 重启）按原项目逐条对齐，映射表见 `docs/00-research-brief.md` 与 `docs/02-architecture.md` 附录 A。

---

## 验证状况

```
dotnet test tests\DirMover.Core.Tests   →  268 通过 / 0 失败 / 0 跳过（11 s）
```

基线演进：236（初版）→ 259（红线补测轮）→ **268**（全仓审计修复轮 +9，明细见 `docs/04-redline-coverage.md`）。数字只用于比对，**不要把 `Skipped` 计入通过**。Core 现为 `net8.0-windows;net48` 双目标，但**测试只在 net8 侧跑一遍**——这是有意的：net48 分支不新增业务逻辑，只做 API 形态适配，它的正确性由"WPF 版能编译 + 能跑通完整迁移"来验，而不是靠把 268 条用例再跑一遍。

> 以上数字全部来自 **Core 层**。界面层的启动验证见「已知偏差 4」——**已通过**。卡了两轮的根因是**发布配置缺陷**（自包含产物没随包带框架 `resources.pri`），不是本项目代码，也**不是"本机不支持自包含"**（上一版这样读，是错的）。

**WPF 单文件版实测（2026-11-14，本机）**：

```
dist\DirMover-3.0.1-wpf-net48\DirMover.exe   1,008,640 B   ← 目录内只有它（无 exe.config、无 pdb）
dist\DirMover-3.0.1-wpf-net48.zip              597,352 B
exe SHA-256  e8d5686733dcf22b7e077d33b15276e203be71d0b0d837e518d93d14f39b8cdb
```

`Deterministic=true` 在这版是**真的可复现**：连续两次 `build.cmd wpf`（中间清过 `dist`）产出的 exe 哈希一致。**zip 的哈希每次不同**——不是构建不稳定，而是归档里带了文件时间戳；要核对就核 exe，别核 zip。

从 `dist` 里**直接跑那个 exe** 截的图（不是从 `bin\` 跑的调试产物）：`docs/ui-redesign-screenshots/wpf-dist-main.png`（964×721，主窗口：表格卡片、状态列着色、空态、按钮组与 WinUI 版逐项对照一致）与 `wpf-dist-dialog.png`（562×338，`--version` 弹窗）。`--scan` / `--dry-run` / 退出码 0·2·3·4 与 WinUI 版同文案同口径。

三个只在这一版出现的坑，记下来避免复踩：

- **`Window.Loaded` 在 `Show()` 之后订阅不会触发**。WinUI 版是 `Loaded` 里挂首次扫描，WPF 版照抄后 `--version` 弹窗永不出现（日志停在"已挂 Loaded"）。改用 `ContentRendered`，并在代码里留了注释说明语义差异。
- **无边框自绘弹窗会先按最小尺寸量一遍**。首版弹窗宽到 1440 px、等宽报告区还居中折行。修法是 `SizeToContent="WidthAndHeight"` + 显式给 `BodyScroll.Width`/`MaxHeight`，等宽块用只读 `TextBox`（`TextBlock` 在 WPF 里不支持选中）设 `TextWrapping.NoWrap` + `TextAlignment.Left`，横向滚动条自己承担溢出。
- **`Closing` 管线里不能再调 `Close()`**。关窗收尾是"先 `e.Cancel = true`，再 `await RequestCloseAsync()`，然后 `Close()`"——但 `await` 的续体经 Dispatcher 恢复时，触发本次 Closing 的那次 Close 还没走完，此时 `Close()` 被 `VerifyNotClosing` 拒绝，抛 `InvalidOperationException` 并触发全局异常弹窗（`startup_error.txt` 有实测记录）。修法：最终关闭延到下一轮 Dispatcher（`CloseDeferred`），那是一次全新的 Close 调用，放行标志已置位。回归脚本 `out/close_test_wpf.py`：WM_CLOSE → 退出码 0、无错误日志。

**视觉对齐轮（2026-11-14）**：界面再向便携版靠了一档——ComboBox 整模板重绘（#FBFBFB 扁平底、4px 圆角、字形箭头、8px 圆角浮层与控件同宽）、勾选框 20px + 勾号弹出动画 + 按下 92% 微缩、键盘焦点环改成 Fluent 双线式、滚动条悬停 8→12px 展开、字体回退链改 `Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI`（Win11 数字与便携版同款，Win10 回落 Segoe UI）。**Mica 试过又撤了**：便携版本身没有 Mica（纯色浅灰底），而 WPF 端做系统 backdrop 需要 `DwmExtendFrameIntoClientArea(-1)` + 透明背景，实测会透出桌面其它窗口内容且文字有合成重影——偏离便携版观感，不做。这一轮全部改动合计 **+4.6 KB**（exe 0.96→0.96 MiB 级），对比图见 `docs/ui-redesign-screenshots/wpf-aligned-main.png` 与 `wpf-aligned-combo-open.png`。顺带修掉了只在 net48 目标暴露的一批可空性警告（net48 引用程序集缺 `NotNullWhen` 注解，`IsNullOrEmpty` 不收窄——判空改写成对两个目标都成立的显式形式，net8 行为不变，268 测全绿）。

**强调色对齐图标（2026-11-14，用户反馈）**：应用图标主体是蓝色渐变（#1870C0→#4090E0），而界面强调色一直是青碧（#0E7C86 系）——"图标蓝、界面青"各唱各的。把两版（WinUI 便携版 + WPF 单文件版）的强调色族整体换成图标蓝：AccentBrush=#1870C0（图标原色）、悬停 #3888D8 / 按下 #1068B8（同取自图标渐变）、链接与状态文字 #0B5394（白底约 7.8:1）、弱底与空态徽章同族淡蓝；深色档整体提亮同色相（#6FB2E8 系）。**语义色不动**：成功=绿、危险=红、警告=黄的语义仍然保留（状态列"完成"仍是绿字）。改的只是 `Theme/Tokens.xaml` 一处真源（外加遗留未接线的 `App/Tokens.xaml` 同步），布局零改动。

**全面审计轮（2026-11-14）**：对单文件版做了一轮逐文件走读 + 实机行为测试（CLI 退出码、单实例、关窗、缩窗滚动条），发现并修掉 6 处（第 7 条为审计后用户实测补充）：


1. **CLI 跑完不退出**（最重要）：`--version` / `--help` / `--scan` / 未知参数在弹窗关闭后留下一个空主窗口，进程永远不结束——脚本调用 `DirMover.exe --version` 会一直挂着。修法：CLI 路径收尾时落退出码并关闭主窗口（`ExitAfterCli` + `CloseForCliExit`），实测四种参数各自以 0/0/2/0 退出。**这是与 WinUI 版的一处刻意分叉**：WinUI 版同样的路径仍需手动关主窗口，本轮只修了单文件版（WinUI 侧未经运行期验证，不动）。
2. **清单缺 `longPathAware`**：WinUI 版清单一直有，net48 上这条比 .NET 8 更关键——.NET 8 会对超长路径自动补 `\\?\` 前缀，net48 不会，没有这条时 ≥260 字符的深目录复制/删除直接抛 `PathTooLongException`。已补。
3. **启动兜底自身会崩**：`DefaultAppPaths.Resolve` 抛异常时 `_paths` 还是 null，`WriteStartupError` 直接解引用 `_paths.StartupErrorFile` 抛 NRE——注释写着"退回临时目录写一行"，代码却没做。已改为 null 安全 + 临时目录兜底。
4. **滚动条样式从未被引用**：`SlimVerticalScrollBarStyle` 定义了但没有任何使用点，列表与弹窗实际显示的是 WPF 经典滚动条（两侧箭头 + 立体滑块）。改为隐式样式（纵向/横向两套模板 + 8→12px 悬停展开），列表、下拉浮层、弹窗正文、等宽报告区共用一份；缩窗到 640 高度实测滑块渲染正常。
5. **回车键默认触发主按钮**：`--version` 弹窗上按回车会去"打开日志目录"（弹一个资源管理器窗口），遗留事务弹窗上按回车会直接执行"回滚"这种破坏性操作。改为默认按钮显式声明：只有"是/否"二选一把回车绑到主按钮，其余默认回车=关闭。
6. **绑定路径静默失效没有诊断**：WinUI 版有 `DebugSettings.BindingFailed`，WPF 的等价物（`PresentationTraceSources`）实测必须在 app.config 声明才真正生效——构造期设 `Switch.Level=All` + 挂 listener 收不到任何事件——而单文件承诺禁止 exe.config。替代方案：新增 `tools/check-bindings.py`，静态解析 XAML 里全部 72 处 `{Binding}` 路径并与 C# 属性表逐段对照（含 `Columns.NameWidth` 多级路径与 DataTemplate 上下文切换），并已接入 `build.cmd wpf` 第一步：路径写错在打包前就报错。
7. **悬停遮罩把整行内容盖没**（审计后用户实测发现）：`FolderRowItemStyle` 容器模板里悬停遮罩 `Border` 声明在 `ContentPresenter` **之后**——WPF 后声明者画在上层——而 `RowHoverBrush` 是不透明色，悬停时整行内容被遮住，行变成一条空灰色带（截图上像两行之间多出一个空槽）。修法：删掉容器层的遮罩，悬停视觉只保留 `ItemTemplate` 里层级正确的那层（状态底色之上、内容之下）；容器模板只留替掉 WPF 默认选中底这一职责。回归脚本 `out/hover_test_wpf.py`：真实移鼠标到行上再 PrintWindow，像素断言悬停色带存在且行内容可见。

另有一处**记录但不修**的共性问题：主窗口的两条水平分隔线（行 2 / 行 5）在两版里都不可见——`RowDefinition Height="1"` 里放一个上边距 16 的 Border，可用高度算出来是负数，整行塌成 0。WinUI 版同样如此（对照两版截图的像素均有且仅有表头线），修它需要改两版布局且 WinUI 侧无法在本机验证，暂不动。

**启动诊断仪表（2026-11-14，用户问"启动能更快吗"）**：启动路径全程加了 Stopwatch 里程碑，首帧后在日志里落一行 `启动分解: 进程→App构造 / App.xaml解析 / MainWindow.xaml解析 / 盘枚举 / VM构造 / Attach / Show / Show→首帧`。实测本机（热启动）：合计 ~2.9s，其中 App.xaml 解析 435ms、MainWindow.xaml 解析 640ms、`Window.Show()` 1225ms、首帧 60ms，业务代码（盘枚举 6ms + VM 构造 41ms）几乎为零——**瓶颈全部是 WPF 框架的每次运行期 JIT**。根因：本机 `.NET Framework NGen 原生映像缓存整体缺失`（`NativeImages_v4.0.30319_64` 目录不存在，mscorsvw 服务也缺失），每个 .NET 4.8 程序每次启动都在重新 JIT 整个框架；重建缓存需以管理员运行 `ngen install`（本机实测被系统拒绝 E_ACCESSDENIED，属 OS 镜像层面限制）。正常 Windows 上缓存齐全，启动远快于此；该仪表留给以后定位启动回归。

**提权级别的刻意差异**：WinUI 版清单声明 `requireAdministrator`（双击即弹 UAC）；WPF 单文件版声明 `asInvoker`——不提权也能完成绝大多数操作（写 HKCU、搬用户目录都不需要管理员），界面顶部会显示提权提示条，真正需要时用"以管理员身份重启"按钮按需提权。这样 `--scan` / `--version` 等只读路径也不会被 UAC 打断。两版行为差异见各自清单里的注释。

12 条数据安全红线：**10 条强覆盖 / 2 条弱覆盖 / 0 条无覆盖**。

- 弱 **红线 6**（空间未知/不足）：单卷下含引擎级强覆盖，**跨卷形态未覆盖**；
- 弱 **红线 11**（`REG_EXPAND_SZ` 判定）：判定逻辑 8 个数据点覆盖，但**真实 `Win32RegistryBackend` 从未执行**，真机才是最终裁决。

历史缺口：**红线 8 的"盘根作目标"曾完全缺失**（`ValidateDestination(@"C:\")` 返回 `Ok=true`），已修复并转为回归用例。逐条对照见 `docs/04-redline-coverage.md`。

**仍在的未覆盖项（不要当成"已验证"）：**

- **跨卷**：完整安全通道已验（`RedLine1_CrossVolume_EndToEnd_KeepsDataAndRemovesSource`，源在 C:、目标在 D:，含空目录、删源、注册表写入、清理断言）。但**跨卷下的"空间不足 / 空间未知"分支仍未单独验**。另注意：该用例在只有一块固定盘的机器上会在输出里打一条 `[ENV-MISSING]` 后返回（xunit 2.9 没有 `Assert.Skip`，v3 才有）——**看到这条标记就说明它没跑过，不许记作通过**；
- **稀疏文件**：`FSCTL_SET_SPARSE` 与跳零块那条分支零覆盖（最坏结果是"搬不完"，不会丢数据——摘要会拦住）；
- **真实 `Win32RegistryBackend`**：全部注册表用例走内存后端，`REG_EXPAND_SZ` 的最终裁决必须在一次性 VM 上手工验一次；
- **源目录本身是指向禁区的 junction**：无端到端用例（`CheckSourceTree` 的链接展开分支是唯一防线）；
- 引擎里两处闸门分支（`ResolvesToSame`、执行期重解析点）当前不可达，靠上游守卫拦住；一旦上游改动，它们是唯一拦截点，却没有用例。

---

## 已知偏差（已接受，不是待办漏项）

1. **文案并非全部出自 `UiText`**：`UiText` 之外仍有 109 处中文字符串字面量。其中日志/异常类可接受；**会直接上屏**的有 `ScanReport`、`FsOps.Verify` 的 `problems`、`TransferEngine` 的 `ItemOutcome.Message`、`PathTools.SpaceLabel`。后果：PRD 的逐条文案对照目前无法只对一个文件做。新增代码必须走 `UiText`，不要顺手扩散。
2. **`IO ⇄ Engine` 命名空间成环**：`FsOps.Copy/Verify` 需要 `ITransferProgress` / `TransferProgressInfo`（定义在 `Engine/PlanData.cs`）。文件级无环、不影响编译；拆法（把两个进度类型搬到中立命名空间）已留档，下轮可执行。**不要**把它"修"成 `Engine → IO` 单向——编排层对 IO 的独占调用权，正是"删源只能在一处发生"能被集中审计的前提。
3. **`ScanReport_HasThreeSections_AndWritesNothing` 名字与输出不符**（实为四段）。内容已由 `RedLine10_ScanReport_RendersWarningsFromRealReadFailures` 覆盖，改名属装饰性清理。
4. **全自包含（`WindowsAppSDKSelfContained=true`）：曾整轮卡在 `0xC000027B`，根因已定位并修复**。产物在**本机**启动即 `EXITED 0xC000027B`，事件日志 `Microsoft.UI.Xaml.dll` / `0xc000027b`（`STATUS_STOWED_EXCEPTION`）。为判定责任归属另建了一个**完全独立的最小 WinUI 3 工程**（不引用本项目任何代码，只有一个 `Window` + 一个 `TextBlock`）：

   ```
   PROBE (WindowsAppSDKSelfContained=false，框架依赖)            → ALIVE  hwnd=2557576  title=[WinUI Desktop]  wsMB=151.6  threads=55
   PROBE (WindowsAppSDKSelfContained=true ，同一工程、同一代包)  → CRASH  exit=-1073741189 (0xC000027B)
          该探针产物实测：2 个 .xbf（App 690 B / MainWindow 527 B）齐全，但 .pri 只有 3 个、缺 resources.pri  ← 等价于「形态 A」
   PROBE (自包含 + 补入框架 resources.pri 1,351,648 B)           → ALIVE  ← 等价于「形态 B」，即本项目交付形态
   事件日志：Microsoft.UI.Xaml.dll 3.1.8.0 / 0xc000027b / 偏移 0x3a7515，Faulting 模块路径落在该探针自己的输出目录内
   UserInteractive=True / SessionId=1 / dwm、explorer 均在 session 1 / notepad 正常存活
   ```

   **方法论（必须一起读）**：上面这组是**受控对照**（唯一变量 `WindowsAppSDKSelfContained` + `resources.pri`）。**探针工程既没有 `CopyXbfToPublish` 也没有 `StageFrameworkPri`**，所以它"自包含也崩"的负结果**两次都不可采信**：第一轮崩在"缺编译后 XAML"（`.xbf` 数 0），第二轮崩在"缺框架 pri"（`.pri` 只有 3 个）。**最小探针必须逐项对齐产品工程的发布链路，否则负结果不可采信**（`docs/02-architecture.md` §9.4）。

   → 结论：**全自包含形态本身可用**；`0xC000027B` 的真因是**产物没随包带框架 `resources.pri`**（**自包含模式下这份 pri 必须显式补入** —— 它不再由系统运行时提供，而构建过程没有把它带进来；**不要把因果锁死在某个开关上**：旧包版本确实是"关掉 MRT 工具链就没人搬 pri"，但本代包上即使 `EnableCoreMrtTooling=true` 也没有自动产出它，所以稳妥的表述是"必须显式补入"），**不是"本机不支持自包含"**，也不是缺 `.xbf`。补上 `resources.pri`（1,351,648 B）后同一份自包含产物 `ALIVE`。复现路径：`%TEMP%\winui-probe-fd\`（框架依赖，活）/ `%TEMP%\winui-probe-self\`（等价形态 A，崩）/ `out\selfcox\`（等价形态 B，活）—— 均为**排障对照产物，非交付物，可随时删除**。另有**门禁负例夹具** `out\gatetest-empty\`（空目录，验"缺文件"分支）与 `out\gatetest-small\`（**1,192 B 的假 pri**，内容为零填充，验"同名不同物"分支：`Test-Path` 会绿、尺寸判据必须红）；**干净构建对照**：`out\verify-clean\`（清空 `bin`/`obj` 后重建的**真干净态**产物，333 / 422 / 510，且**不含**那个 winmd）、`out\quarantine-binobj\`（移走的旧 `bin`/`obj`，里面能看到 1.5 代遗留的 `Microsoft.Web.WebView2.Core.winmd` —— 用来复现"为什么必须清中间产物"）——**负例夹具，勿删，勿当作产物**：它们不可运行，误当产物会引出新的误判；它们的**存在本身就是"门禁被验证过"的物证**，比任何人的一句"验过"强。

   已排除的假设（**不要重复尝试**）：

   | 假设 | 怎么验的 | 结果 |
   |---|---|---|
   | 缺编译后的 XAML（`.xbf`） | `find publish -name "*.xbf"` | 4 个齐全，**仍崩** → 但它**是必要条件**：`.xbf` 为 0 的探针同样崩，且崩因不同（第一轮误判就错在这里） |
   | 缺 `resources.pri`（**应用** pri） | `makepri.exe` 对应用自身资源生成 1192 B 后启动 | **仍崩** → 排除的是**应用 pri**（项目零 `.resw`，`makepri` 永远产不出框架 pri）；**框架 pri（1,351,648 B）才是根因**，详见 `02-architecture.md` D-11 |
   | Debug 缺 .NET 8 运行时 | `DOTNET_ROOT` 指向本地 8.0.11 后启动 | 仍崩 → 排除（这条只解释"为什么之前没发现"） |
   | 资源字典 / 转换器 / 最小窗口 | 二分法 | **至今未做**；但**必要性已被证伪** —— 补齐发布链路（`.xbf` + 框架 pri）后即可正常启动，真正的差异不是资源字典 |

   **交付形态（现行口径）**：**全自包含** —— `.NET 8` 与 WinUI 1.8 都在包内，**目标机零前置依赖**（不需预装 .NET，也不需预装 Windows App Runtime）。**上一版要求目标机预装 Windows App Runtime 1.8（含 DDLM），现已不需要**。框架 pri 由构建目标的**唯一取源** `Microsoft.WindowsAppRuntime.<ver>.msix`（NuGet 缓存里的 `microsoft.windowsappsdk.runtime` 包）取出并复制进输出目录；**找不到 MSIX 时构建直接失败（fail-closed）**，不会产出"能构建、运行必崩"的产物。两个包版本必须一起动：`Microsoft.WindowsAppSDK 1.8.260921001` 要求 `Microsoft.Windows.SDK.BuildTools >= 10.0.26100.4654`（否则还原即 `NU1605`）。

   **已通过的启动验证**（**最终产物**：`publish\DirMover.App.exe` **630,272 B**，两代同尺寸：2026-11-12 `23:03:11` 交付代 ＋ 2026-11-13 `08:32` 用当前源码且清空 `bin`/`obj` 后的复核代，复跑结论相同）；交付态 `publish\` 顶层文件 **333** / 顶层条目 **424** / 递归 **513**、4 个 pri 齐。**计数口径**：顶层文件=仅顶层文件不递归（`ls -Ap` 去掉目录项）、顶层条目=文件+目录、递归文件=`find -type f`；**该时点已启动过 1 次并跑过 2 次 `--scan`**，比真干净产物（`333 / 422 / 510`）多的部分是 2 个运行时目录（`logs\`、`backups\`）+ 3 个文件（1 条日志 + 2 份 scan 报告）。先前流传的 `334 / 425` 出自**脏 `bin`/`obj`**：多出的那个是 `Microsoft.Web.WebView2.Core.winmd`，来自 1.5 代包（1.8 包内零 winmd、MSIX 内也没有），**已作废**：

   ```powershell
   $p = Start-Process publish\DirMover.App.exe -PassThru; Start-Sleep 6; $p.Refresh()
   if ($p.HasExited) { "CRASH " + $p.ExitCode } else { "ALIVE"; ... }
   ```

   ```
   ALIVE
   hwnd=1312754  title=[DirMover — 用户数据转移工具]  wsMB=164  threads=57
   logs\DirMover-20261112.log 已生成        （← 下面三条才是判据，hwnd 只是那一瞬的旁证）
   pri: resources.pri 1,351,648 | Microsoft.UI.Xaml.Controls.pri 1,258,296
        Microsoft.UI.pri 101,528 | Microsoft.WindowsAppRuntime.pri 8,648
   modules(wide) : 8 个，local=8 / other=0        （narrow 口径为 3 个；两个数都对，差别在匹配模式，t=3/10/16 s 均相同）
   modules 判据   : 只看 other=0（个数不带口径没有意义）
   ```

   **模块判据只看 `other=0`**（没有任何 WinUI 模块来自 `C:\Program Files\WindowsApps\`），**不看个数**，且**报数必须带口径**：过滤 `Microsoft.UI*` + `Microsoft.WindowsAppRuntime*` + `Microsoft.Graphics*`（**wide**）是 **8 个**；只过滤 `Microsoft.UI.Xaml*` + `Microsoft.WindowsAppRuntime*`（**narrow**）是 **3 个** —— 差的 5 个（`Microsoft.UI.Windowing.Core.dll`、`Microsoft.UI.Composition.OSSupport.dll`、`Microsoft.UI.Input.dll`、`Microsoft.UI.Windowing.dll`、`Microsoft.UI.dll`）名字不以 `Microsoft.UI.Xaml` 开头。**这两个数从 t=3 s 到 t=16 s 都不变**，所以它不是"按需加载"的时间差（上一版正是这么误解释的）。另：`$p.Modules` 在**非提升**会话里读到**空集**，**空集不能当"非自包含"的证据**，须显式区分 `EXITED` / `MODULES-UNREADABLE` / `local+other` 三态。

   **判活只认三条**：标题**逐字**等于本程序的 `WindowTitle`（不是 `DirMover.App.exe`）＋ `logs\DirMover-yyyyMMdd.log` **被创建** ＋ **线程数量级 57 vs 8**。**`hwnd` 非 0 不是判据** —— apphost 的 `.NET 缺失` 对话框同样有 `hwnd`（QA 实测 `4,850,954`）且进程不退出（**进程活着不等于程序起来了**：假活样本是 27.4 MB / 8 线程 / 标题为 exe 名 / **`logs\` 未生成**，真身是 161–178 MB / 57–58 线程 / 正确标题 / 日志已落地；两者用"存活"或"有窗口"都分不开）。

   `--scan` 只读通路也跑通了：`publish\logs\scan-20261112-225850.txt`，7 项目录 + 2 块磁盘 + 注册表 20/31 个值 + 「读取告警：无」；`logs\DirMover-*.log` 里**没有一条「绑定失败」**，`binding-failed.log` 未生成。`{Binding}` 61 处 / `x:Bind` 0 处（行模板没有混进 `x:Bind`，全部走 `DataContext`）。

   **跨形态一致性（行为证据，比"进程活着"强一档）**：同一命令在**框架依赖**产物（`scan-20261112-221215.txt`）与**全自包含**产物（`scan-20261112-225850.txt`）上各跑一次，**报告 15 行中 13 行逐字相同，仅 2 行不同**：① 标题时间戳；② `D:\` 可用空间 `117.6 → 116.7 GB`（本轮两次构建写盘造成，**属预期**）。⇒ 扫描通路的行为**不随产物形态变化**。**注意**：正因为报告里嵌了可用空间，它**不能当"环境未变"的证据** —— 任何 `diff` 两次 scan 报告的判据，都必须先归一化「时间戳」与「可用空间」两行，否则必然假红。

   **仍未验证**：勾选 → 主按钮 → 确认 → 完成的**写操作**主流程。本机当前 7 项目录都已指向 `D:\UserData`（"路径相同，无需转移"），跑一遍等于什么都不做；真跑要用可丢弃的 VM。**不要**把上面这条"启动 + 只读扫描通过"记成"主流程验证通过"。静态契约（`Frame`/`NavigationView` 计数 0、唯一 `AccentButtonStyle`、4 处 `ToolTip`、`x:Name="Root"` 存在、正文零中文字面量、321 行布局、全量重建 0 警告 0 错误）依然只是契约，不替代运行。

   **仍未验证（第二条，别当成已验）**：`Clean` → `publish` 的完整链路。**按设计应成立**（`Clean` 删 `OutDir` → `Publish` 依赖 `Build` → 重新触发 `StageFrameworkPri` → `CopyXbfToPublish` 再从 `OutDir` 取），但**没有实测**（冻结期内没跑，跑了会刷掉 `publish\` 的 mtime 与文件计数）。另：`dotnet publish --no-build` 会被 `VerifyFrameworkPriInPublish` 拦住 —— **已知且刻意**，`--no-build` 等于"拿没被门禁验证过的既有输出发布"，与"缺 pri 就失败"的目标直接冲突。

   **"属性可以少，目标不能少"**：本代包 `build` 零额外属性、`publish` 只需 `--self-contained`，但 csproj 里的 `StageFrameworkPri` / `CopyXbfToPublish` / `VerifyFrameworkPriInPublish` **一个都不能删** —— 它们补偿的正是"关掉 MRT 工具链后没人搬框架 pri"这件事（见 `02-architecture.md` §8-R6 的限定 ③④）。`CopyXbfToPublish` **名字沿用历史名**，现在同时负责把框架 `resources.pri` 带进 `publish\`，看到名字别以为它只搬 `.xbf`。

完整偏差与判定见 `docs/02-architecture.md` §9.3（D-1…D-12）与 §9.4（**环境与产物前提**：环境类=换机必重验，产物类=每次 publish 必验）。

---

## 文档

| 文件 | 内容 |
|---|---|
| `docs/00-research-brief.md` | 原始项目行为基线：7 项规格、9 条规划校验、事务、11 条实现要点、12 条可验证红线 |
| `docs/01-prd.md` | 产品目标、用户故事、红线不做清单、功能点与界面映射、文案契约 |
| `docs/02-architecture.md` | 分层、文件清单、签名级接口、时序图、任务分解、跨文件约定、风险与偏差记录 |
| `docs/03-acceptance-checklist.md` | 验收清单（含「糊弄形态」列：这条最可能被怎样假通过） |
| `docs/04-redline-coverage.md` | 12 条红线 → 用例 → 判定的逐条对照 |
| `docs/class-diagram.mermaid` / `sequence-diagram.mermaid` | 类图与三条主流程时序图 |

---

## 许可

原始项目为 MIT。本重写沿用同一许可；`Assets/app.ico` 取自原始项目。
