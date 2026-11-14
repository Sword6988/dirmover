# DirMover 原项目研究简报

> 研究对象：`https://github.com/Sword6988/dirmover`（本地克隆：`repo-src/`，源码 `repo-src/pydm/`）
> 版本：v3.0.1（MIT，作者 ajian）｜技术栈：Python 3.10+ 标准库（tkinter + winreg）｜交付形态：单文件 `DirMover.exe`（PyInstaller，约 12.4 MB，内嵌 CPython 3.14）
> 本简报为 WinUI 3 + C# 重写的**行为基线**：重写的判定标准是"行为逐条对等"，不是"界面相似"。

---

## 1. 一句话定位

把 Windows **当前用户**的个人文件夹（文档、收藏夹、图片、音乐、视频、下载、桌面共 7 项）从系统盘迁移到其它磁盘，并同步修正注册表，使系统与资源管理器继续正常工作。效果等同资源管理器的「属性 → 位置 → 移动」，差别是**一次可处理多项**，且把数据安全作为第一优先级：

```
规划 → 复制（含空目录/链接重建）→ 源↔目标全量校验 → 写注册表 → 校验通过才删源
```

任一步失败，**源目录一定还在**。

---

## 2. 核心功能清单（行为契约）

### 2.1 可转移文件夹规格（`constants.FOLDERS`，7 项，顺序即界面顺序）

| key | 显示名 | 权威值名 | 需同步的别名 | 系统默认路径 |
|---|---|---|---|---|
| `documents` | 我的文档 | `Personal` | `{F42EE2D3-909F-4907-8871-4C22FC0BF756}` | `%USERPROFILE%\Documents` |
| `favorites` | 收藏夹 | `Favorites` | `{1777F761-68AD-4D8A-87BD-30B759FA33DD}` | `%USERPROFILE%\Favorites` |
| `pictures` | 我的图片 | `My Pictures` | `{0DDD015D-B06C-45D5-8C4C-F59713854639}` | `%USERPROFILE%\Pictures` |
| `music` | 我的音乐 | `My Music` | `{A0C69A99-21C8-4671-8703-7934162FCF1D}` | `%USERPROFILE%\Music` |
| `videos` | 我的视频 | `My Video` | `{35286A68-3C57-41A1-BBB1-0EAE73D76C95}` | `%USERPROFILE%\Videos` |
| `downloads` | 下载目录 | `{374DE290-123F-4565-9164-39C4925E467B}`（GUID 即权威值名） | `{7D83EE9B-2244-4E70-B1F5-5393042AF1E4}` | `%USERPROFILE%\Downloads` |
| `desktop` | 桌面 | `Desktop` | `{754AC886-DF64-4CBA-86B5-F7FBF4FBCEF5}` | `%USERPROFILE%\Desktop` |

写入位置（HKCU）：

- 权威子键 `Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders`
- 附加同步 `...\Explorer\Shell Folders`（**只写已存在的值**，绝不新建；作用是让改动免重启资源管理器生效，系统可能覆盖回去，不影响数据安全）
- 别名同样**只更新注册表中已存在的值**，不凭空创建

> 高风险项（Cookies / 缓存 / 历史 / 最近文档 / TEMP）已从产品中整体移除，重写**不得**加回。

### 2.2 目标路径合成

- 目标根目录 = 固定下拉，取值恒为 `X:\UserData`，X 为**非系统固定盘**（`DRIVE_FIXED`；U 盘/光驱/网络盘不算）
- 勾选「创建当前用户目录」→ `<目标根>\<USERNAME>\<源目录基名>`；不勾选 → `<目标根>\<源目录基名>`
- 基名取**当前实际路径**的最后一段（不是显示名）

### 2.3 规划期校验（引擎层，UI 与 CLI 同一条防线）

拒绝并给出中文原因：

1. 目标路径为空 / 含非法字符（`< > " | ? *`，`:` 仅盘符位合法）
2. 非绝对路径、缺盘符、目标磁盘不存在
3. 路径片段是 Windows 保留名（`CON`/`PRN`/`AUX`/`NUL`/`COM1‑9`/`LPT1‑9`，含上标数字变体，含 `CON.txt` 形式）或以点/空格结尾
4. 目标落在系统目录（系统盘下 `Windows` / `Program Files` / `Program Files (x86)` / `ProgramData` / `PerfLogs` / `Recovery` 整树；`Users` 仅精确匹配）——名单按 `%SystemDrive%` **动态**构造，且做 8.3 短名还原后再比
5. 目标位于源之内 / 源位于目标之内 / 目标与源**实际指向同一物理位置**（junction、符号链接、subst）
6. 目标自身或**任一级已存在上级目录**是重解析点（`reparse_ancestor`）——必须在**执行期**再查一次（规划与执行之间存在分钟级时间窗）
7. 源位置本身是盘根 / 系统目录（注册表是外部可写数据，被改成 `C:\Windows` 时必须拒绝"整目录搬走再删掉"）
8. 空间闸门：`needed × 1.05 + 64 MB` vs 可用空间；**可用空间读不到 → 阻断**（不得当成"无需检查"）。同卷且目标不存在时走原子 rename，**零字节需求**，不参与空间校验
9. 统计期存在不可读项（`unreadable > 0`）→ 阻断（规模被低估，不能据此判断"空间充足"）

### 2.4 执行事务（每项，顺序不可颠倒）

**路径 A：同盘且目标不存在** → `MoveFile`/`rename` 原子移动，一步到位无中间态。

**路径 B：安全通道**

1. **清点源**：`os.walk` + `onerror`，枚举全部文件（size + mtime_ns + dev/ino/nlink/attrs）、**空目录**、符号链接/目录联接；任一读不到即**整体失败**（不漏项才敢删源）
2. **重建目录结构**（含空目录）与**链接**（按原样重建，不跟随内容；建不出来即整体失败）
3. **复制**：1 MiB 分块读写，**边复制边算源内容摘要**（blake2b‑128）；稀疏文件按洞复制（`FSCTL_SET_SPARSE` + 跳零块 + truncate）；硬链接按 `(dev, ino)` 在目标重建（`ino==0` 时不参与，防 SMB/exFAT 误合并）；**同名冲突绝不覆盖**——内容一致则复用（必须比内容，不能只比 size+mtime），不一致则另存 `name (1).ext`，两份都保留；每个文件先写 `.part` 临时名，成功后 `os.replace` 原子改名（取消/崩溃不留半成品）
4. **源 ↔ 目标全量校验**（删源前唯一闸门）：目标存在？目标 size == **源** size？目标摘要 == **源**摘要？源自清点以来未被改动（size+mtime）？重新枚举源后「源集合 − 清单集合」为空（目录/文件/链接分别比对）？清单里的目录/链接在目标侧齐备（链接用 `lexists`，避免悬空链接导致永不通过）？无「按 skip_names 跳过却未复制」的项？
5. 校验通过 → **写注册表**（本项全部值一起写；任一失败即把本项**已写入的值就地回滚**，杜绝"权威值已改、GUID 别名未改"）
6. 注册表写成功 → **删源前最后两道闸门**：① 源与目标是否实际指向同一物理位置；② 重新清点源，名字集合 + `(size, mtime_ns)` 指纹必须与清单完全一致（防"校验读完→原地改写同长度内容→删源"）
7. 最后删除源目录（只读属性→清属性重试一次；WinError 5/32/33→整树退避重试，共 3 次、累计 < 1 秒；其余错误立即失败）

**中断安全**：每项阶段登记到 `backups\intent.json`（原子写：先 `.tmp` 再 `os.replace`）；启动时若文件还在 → 说明上次未走完，弹窗告知并提供"用快照回滚注册表"。`pending`/`cancelled` 阶段在收尾时注销，只有**真正半完成**的项保留。

**快照**：每次执行**开头一次性**把本次计划涉及的全部注册表原值存成一份 `backups\registry-YYYYMMDD-HHMMSS.json`；执行中逐项用它的内存态做失败回滚。快照写不进去**不阻断**搬运，但必须在结果里告知"本次无法一键回滚"。

**回滚边界**：快照回滚**只还原注册表值，不搬回文件**；要搬回文件走「恢复系统默认值」。回滚前用「未完成项涉及的 (子键, 值名)」裁剪快照，避免把本轮已成功的项滚回旧路径（否则注册表指向不存在的目录）。快照期**读失败**的条目记 `unknown`，回滚时**跳过**（既不能恢复也不能删除）。

### 2.5 恢复系统默认值（完整恢复，不只是改注册表）

1. 目标 = `spec.default`（保留 `%USERPROFILE%` 形式写入注册表，与系统原生写法一致）
2. 内容从当前位置**搬回**默认目录，走**与转移完全相同**的安全事务（复制→校验→写注册表→校验通过才删源）
3. 安全边界：空间不足的项**单独阻断**并报缺口，其余照常；注册表指向盘根/系统目录，或与默认目录互为祖先/后代 → 该项拒绝；源目录已不存在 → 降级为**仅修正注册表**（`registry_only`，不动任何文件）

### 2.6 系统集成

| 能力 | 行为 |
|---|---|
| 管理员 | 进程 manifest `requireAdministrator`；非管理员时顶部警示条（Banner 内提供"以管理员身份重启"，`ShellExecute runas`） |
| 重启资源管理器 | 读 **HKLM** `Winlogon\AutoRestartShell`（`=1` 或不存在→允许；`=0`→跳过并说明；读失败→跳过，fail-closed）；`taskkill /f /fi "username eq DOMAIN\user" /im explorer.exe`（**只杀本用户会话**，参数数组传递）；结束后复查进程是否回来，没回来用 `ShellExecuteExW`（**不继承提权令牌**）拉起；参数：超时 20s + 静置 1.5s + 存活确认 1.0s |
| 单实例 | 命名互斥体 `Local\DirMover_Single`（按登录会话隔离）+ 持有者 PID 落 `%LOCALAPPDATA%\DirMover\single_instance.pid`；"已存在"不直接阻断，还要确认持有者是**活着的本程序**（防被普通进程抢占造成拒绝服务）；提权重启前先释放互斥体并清 PID 文件 |
| 打开目录 | 状态栏「打开日志目录」用 `explorer.exe <path>`；目录不存在时改口径报"日志目录不可用" |
| 环境广播 | `SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, "Environment")`，当前**未接线**（无 TEMP/TMP 项），属预留能力 |

### 2.7 日志与产物

- `logs\DirMover-YYYYMMDD.log`（文件 + stderr），过程详情**只写文件**，界面无日志区
- `logs\console.log`（无控制台时 CLI 输出落盘）、`logs\startup_error.txt`（启动崩溃兜底）
- `backups\`：快照 + 意图日志；**程序不读写任何配置文件**（每次启动用内置默认值）

---

## 3. 交互流程

### 3.1 界面骨架（单屏、零导航、7 行单列网格）

```
行0 AdminBanner      非管理员时显示：⚠ 提示 + [以管理员身份重启]
行1 DestinationBar   目标根目录 [只读下拉]            D:\ 可用 1.2 TB / 2.0 TB
行2 Separator
行3 FolderTable      勾选列 | 文件夹名称 | 当前位置 | 转移目标 | 状态   ← 唯一伸缩区(weight=1)
行4 ActionBar        ☑全选/全不选                 已选择 7/7 项        [开始转移]
                     ☑创建当前用户目录        [恢复系统默认值]   [退出程序]
行5 Separator
行6 StatusBar        就绪        [打开日志目录](条件)  [====----]  试运行模式
```

硬约束：不切页、不弹二级窗口、界面内**没有**日志查看区；唯一强调色按钮是主操作；副按钮/危险按钮分层见原 `docs/ui-skeleton.md §5`。

### 3.2 主流程

1. 启动 → DPI 感知 → 单实例判定 → 建窗口 → 读注册表算出 7 项当前位置与目标（**不扫描规模**，勾选零读盘）→ 全选、`D:\UserData`
2. 用户勾选/改目标根/改「创建当前用户目录」→ 仅重算目标路径与状态列，不读盘
3. 点「开始转移」（或 `Ctrl+Return`）→ 校验目标 → 无可转移项则提示"无需转移" → 确认弹窗（项数 + 目标 + 说明）→ 同时把本次将转移的行**浅蓝底**标出，可逐行核对 → 取消则恢复
4. 后台线程执行：逐项 `处理中…` → 行强调色；进度条 + 「正在处理 X（n/N）」，进度上报按 ≥100 ms 节流；主按钮变「停止」
5. 「停止」→ 置取消位 → 当前项收尾（已完成保留，未完成不删源、不写注册表）→ 剩余项标「已取消」
6. 完成 → 结果弹窗：成功 `操作完成`；有失败 `部分项目未完成`（逐项列原因）+ 状态栏出现「打开日志目录」；取消 `已停止`；状态栏显示摘要（`完成 N 项，失败 N 项，跳过 N 项`）
7. 有成功项 → 询问「是否重启资源管理器」→ 后台执行（不可中断，主按钮显示「重启中…」并禁用）

### 3.3 键盘

- 表格聚焦：`空格` / `回车` 切换勾选（`Ctrl+Return` 不切换，让全局键生效）
- `Ctrl+Return`：开始转移
- 点击 `#0`/名称列切换勾选；**表头不可拖拽改宽**（宽度由内容自适应算法决定）
- Tab 顺序：目标根下拉 → 表格 → 全选 → 创建用户目录 → 主按钮 → 恢复默认值 → 退出

### 3.4 状态与用色（行级）

| 行状态 | 表现 |
|---|---|
| 奇偶行 | 斑马纹（`row_alt` / `surface`），行高 40 |
| 未勾选 | 文字 `text_disabled` |
| 本次将转移 | 浅蓝底 `pending` |
| 处理中 | 强调色 `active` |
| 完成 / 失败 | 绿 `done` / 红 `failed`（上一次执行结果着色，刷新后保留） |

### 3.5 关键弹窗（共 14 类）

无法开始（warning）｜无需转移（info）｜确认开始转移（yes/no）｜恢复系统默认值（yes/no）｜已停止（info）｜部分项目未完成（warning）｜操作完成（success）｜重启资源管理器（yes/no）｜执行出错（error）｜执行中退出（yes/no）｜上次转移未正常收尾（yes/no/cancel）｜回滚成功/失败（success/error）｜提权提示（info）｜重复启动（warning）

### 3.6 退出收尾（按钮与窗口 X 同一条路径）

1. 空闲 → 直接销毁
2. 执行中 → 确认弹窗（可中断任务提示"建议先点停止"；不可中断任务提示"最长约 20 秒，含等待合计约 22.5 秒"）→ 置取消位 → 状态栏「正在安全收尾」→ 每 200 ms 轮询，等 `busy` 与 `finishingUi` 都结束才销毁；**上限 60 s**，超时记日志并强制退出（此时最坏结果是源目录残留空壳，不是目标缺内容）
3. 已请求退出期间：不弹结果窗、不弹错误窗、**绝不执行 taskkill explorer**

### 3.7 启动时未完成事务

启动 400 ms 后读 `backups\intent.json`：

- 读取失败/损坏 → 警示框（无法判断是否有半完成项，请人工核对）
- 有残留项 → 三选弹窗：是＝用快照回滚注册表（按未完成项裁剪）；否＝忽略并清除记录；取消＝保留记录下次再提示
- 回滚结果需报出「恢复 N 处 / 删除 N 处 / 因原值未知跳过 N 处」

---

## 4. 配置项（纯内存，不落盘）

| 配置 | 默认值 | 语义 |
|---|---|---|
| `destination` | `D:\UserData` | 目标根目录；D 盘不存在时回落到首个非系统固定盘；无非系统固定盘 → 空态（下拉空 + 提示 + 主按钮禁用） |
| `create_user_dir` | `true` | 是否在目标中插入 `<用户名>` 一层 |
| `selected` | 全选 7 项 | 勾选集合；只保留已知 key、去重、保持 FOLDERS 顺序 |
| `dry_run` | 由 `--dry-run` 决定 | 试运行：完整规划与判定，但不落任何改动；状态栏显示徽标；不提供提权重启入口 |

---

## 5. 整体架构（原项目）

```
run.py                     入口：GUI / --scan（只读扫描）/ --dry-run（完整规划不落地）
                           单实例、DPI、控制台编码与无控制台落盘、退出码 0/2/3/4
docmover/
  constants.py             唯一真源：APP_VERSION、7 项规格、GUID、注册表子键、空间余量、
                           重启/删源等待、风险等级、禁区名单（按 SystemDrive 动态构造）
  config.py                运行时设置（内存态）+ app_dir() + 目标根构造
  registry.py              RegistryBackend 协议（WinReg / Memory 两实现）、RegistryStore
                           （读失败与"值不存在"分离、REG_EXPAND_SZ 判定、快照 / 回滚）
  fsops.py                 长路径前缀、盘枚举、路径校验、清点、摘要、复制、校验、删源
  transfer.py              业务编排：PlanItem / ItemOutcome / TransferReport / IntentJournal
                           / TransferEngine（build_plan / execute / build_restore_plan /
                           restore_defaults）+ 快照裁剪纯函数
  shell.py                 管理员判定与提权、重启资源管理器、打开目录、环境广播、
                           版本探测、Desktop.ini（预留未接线）
  logging_setup.py         文件日志（可注入目录，失败可感知）
  ui/theme.py              Fluent 浅色令牌（COLORS / SP_XS..XL / px() / 字体表）
  ui/icons.py              运行时渲染线性图标（勾选框、chevron、状态图标）
  ui/columns.py            列宽分配纯函数 fit_columns(needs, minimums, quotas, available)
  ui/main_window.py        主窗口（7 行网格）+ 线程/队列 + 退出收尾 + 事务恢复
  ui/dialogs.py            主题化模态弹窗（show_info/warning/error/success/ask_yes_no/…）
tests/                     8 个测试文件（core / columns / dialogs / entrypoints / hardening
                           / safety_fsops / safety_integration / ui_smoke）
tools/                     ci_check.py（编译+单测+红线守卫）、verify_release.py、build.py…
```

依赖方向严格单向：`constants ← config/registry/fsops ← transfer ← ui`；`shell` 只依赖 `constants/fsops/registry`；UI 不直接碰注册表或文件系统。

**数据流**：UI 收集参数（主线程）→ `build_plan`（只读，读注册表 + 合成目标 + 校验）→ `execute`（后台线程，回调上报进度/逐项状态）→ 队列回传主线程渲染 → 报告 → 结果弹窗 + 可选重启 explorer。

---

## 6. 关键实现要点（重写必须逐条保留）

1. **摘要属于源，不属于目标**：校验若只比对"目标自己的大小"，复制失败也会通过——这是原项目的 P0 级历史缺陷。
2. **复用也必须比内容**：目标已有同名文件、size+mtime 都相同，仍必须比内容摘要；否则"时间戳巧合相同、内容不同"会被当成副本，删源后永久丢失新版本。
3. **清点不漏项**：`os.walk` 必须接错误回调，读不到的目录/文件计入 `unreadable` 并阻断；空目录、链接、清单外文件都参与集合差集。
4. **原子落盘**：文件复制先写 `.part` 再原子改名；意图日志先写 `.tmp` 再原子替换；同盘迁移用原子 rename。
5. **重解析点（junction/符号链接）三道闸**：规划期、执行期（建目录前）、复制内部（目标自身与子目录）；判定必须看**祖先链**，否则 `mklink /J` 会把管理员写操作导向别处后连源一起删。
6. **长路径**：≥240 字符自动加 `\\?\` / `\\?\UNC\`；只在真正调 Win32 时加，比较与 `relpath` 一律用干净路径；`\\?\` 路径不做 `.`/`..` 归一化。
7. **短名与盘符别名**：8.3 短名（`PROGRA~1`）与 subst 盘符必须还原/穿透后再判禁区；`samefile` 优先，`realpath` 兜底。
8. **注册表值类型**：仅当值**以** `%VAR%` 开头且其后紧跟分隔符/结束时写 `REG_EXPAND_SZ`；值中间出现 `%` 一律 `REG_SZ`（否则读回被二次展开成错误路径）。
9. **HKLM 只读、HKCU 只写**：写路径永不越界到机器级配置；读 HKLM 的开关失败时 fail-closed。
10. **不因一项失败停下全部**；同名冲突永不静默覆盖；`explorer.exe` 只在设置允许时优雅重启且只结束本用户会话。
11. **所有"静默失败"都要变成可见事件**：读注册表失败、日志写不进去、删源重试耗尽、校验截断（"共 N 项，以下仅列前 K 条"）都必须上报，不许假装成功。

---

## 7. 已知边界（有意为之，重写应保持一致并在文档中声明）

- 不做设置持久化；不写 `Desktop.ini`；不做倒计时/自动执行；无日志查看区；已移除高风险文件夹项
- 稀疏判定粒度为 1 MiB：块内含少量非零数据会整块写入，**目标占盘可能略大于源**，内容逐字节一致
- 稀疏/硬链接属尽力而为：目标卷不支持时回退完整复制（变大但正确）
- 删源仍可能失败（explorer/索引/杀软持有句柄）：数据安全，源目录只剩空壳，重启后手动删除
- 不使用 `Global\` 互斥体：同一登录会话唯一，多用户/多会话各能跑一份
- 混合 DPI：原实现为 system-DPI-aware（拖到不同缩放副屏会位图拉伸）
- Windows PE 下不支持运行

---

## 8. 从 Python 到 WinUI 3 + C# 的映射建议（供架构师取舍）

| 原实现 | C# / WinUI 3 对应 |
|---|---|
| `winreg` | `Microsoft.Win32.Registry`（`RegistryKey`/`RegistryValueKind`）；仍保留 `IRegistryBackend` 抽象 + 内存实现供测试 |
| `os.walk` + `onerror` | `Directory.EnumerateFileSystemEntries` + 显式捕获 `UnauthorizedAccessException`/`IOException`；或 `EnumerationOptions{ IgnoreInaccessible=false, AttributesToSkip=0 }` |
| `os.stat` 指纹 | `FileSystemInfo`/`File.GetLastWriteTimeUtc` + `File.OpenHandle`；硬链接需 `GetFileInformationByHandle`（`nFileIndexHigh/Low`、`NumberOfLinks`） |
| blake2b‑128 分块摘要 | `System.IO.Hashing` 无 blake2；建议 `IncrementalHash.CreateHash(HashAlgorithmName.SHA256)`（1 MiB 分块）或自实现 BLAKE2b；**摘要只用于同源比对，算法可变但要写进文档** |
| `ctypes` 调 Win32 | `P/Invoke`（`DeviceIoControl` 设稀疏、`GetLongPathNameW`、`GetDriveTypeW`、`ShellExecuteExW`、`SendMessageTimeoutW`）或 `Microsoft.Windows.CsWin32` 源生成器 |
| 原子 rename | `Directory.Move` / `File.Move(overwrite:false)`；跨卷自动回退复制 |
| `shutil.rmtree(onexc=…)` | 自实现递归删除（清只读属性 + 退避重试），不要用 `Directory.Delete(recursive)`（无钩子、错误不可分类） |
| 命名互斥体 | `new Mutex(true, @"Local\DirMover_Single", out createdNew)` + PID 文件校验 |
| tkinter 单屏 7 行 | `Grid`（RowDefinitions 7 行，仅列表行 `*`） |
| `ttk.Treeview` | `ListView`（`ItemsSource` + `ItemTemplate` 内 4 列 `Grid`），或 `ItemsRepeater` + 自管选中 |
| 列宽自适应 `fit_columns` | 纯函数移植为 `ColumnFitCalculator`（同签名），UI 侧把结果写到共享 `TableColumnLayout`（`GridLength` 绑定到表头与每行） |
| 主题令牌 | `ResourceDictionary`（`SolidColorBrush`/`x:Double`），`ThemeResource` 支持浅/深色；`px()` → 用 `x:Double` + `XamlRoot.RasterizationScale` |
| 自绘模态弹窗 | `ContentDialog`（`PrimaryButtonStyle`/`CloseButtonStyle`，`XamlRoot` 必填）；"是/否/取消"用 `Primary/Secondary/Close` 三键 |
| 线程 + 队列 | `Task.Run` + `IProgress<T>` + `CancellationToken`；UI 更新用 `DispatcherQueue.TryEnqueue`（或 `await` 回 UI 上下文） |
| 关窗收尾轮询 | `AppWindow.Closing` → `args.Cancel = true` → 取消并 `await` 收尾（`Task.WhenAny(finish, Delay(60s))`）→ 再 `Close()` |
| PyInstaller 单文件 | `dotnet publish -r win-x64 --self-contained` + `WindowsAppSDKSelfContained=true`（可选 `PublishSingleFile`） |

---

## 9. 规模与实测参考（用于性能与进度设计）

- 真实机器实测：7 项合计约 **48.7 万文件 / 26.1 GB**
- 全量流程含 **4 次全树遍历**（清点 1 + 复制 1 + 校验 1 + 删源前复查 1），实测约 9.1 次 `stat`/文件
- 校验阶段实测 19,953 文件耗时 52.5 s → 进度与取消**必须**贯穿复制与校验两个阶段，否则界面长时间"假死"
- 设计取向：勾选零读盘；规模由引擎在执行时权威扫描；空间闸门在执行前把关

---

## 10. 可验证的红线（重写必须写测试）

1. 复制未完成/校验失败 → **不删源、不写注册表**
2. 同名不同内容 → 两份都在，且记录冲突
3. 空目录必须被复制；丢失即校验失败
4. 源已删除或新增文件 → 校验失败、保留源
5. 写注册表失败 → 本项已写入的值回滚，源目录保留
6. 空间未知 → 阻断；空间不足 → 只阻断该项
7. 目标在源内 / 源在目标内 / 同一物理位置 → 拒绝
8. 盘根 / 系统目录作为源或目标 → 拒绝（含 `\\?\` 前缀与 8.3 短名形态）
9. 快照期读失败的条目 → 回滚时跳过，不得 `DeleteValue`
10. 意图日志：原子写、损坏可感知、取消项不残留、完成项注销
11. `%VAR%\...` 才写 `REG_EXPAND_SZ`；值中间 `%` 写 `REG_SZ`
12. 列宽算法：各列之和恒等于可用宽、保护列（名称/状态）不被压缩、同输入同输出
