using DirMover.Core.Engine;
using DirMover.Core.IO;

namespace DirMover.Core;

/// <summary>
/// 全部面向用户的字符串的<b>唯一出口</b>：状态列文案、状态栏、摘要、阶段标签、
/// 弹窗标题正文、按钮文案、ToolTip。
/// </summary>
/// <remarks>
/// 为什么文案必须集中：PRD §6 的文案契约是**可逐条断言的验收项**（"状态列 5 分支"、
/// "失败文案必须给出下一步"）。文案散落在 XAML 与 ViewModel 里时，只能靠肉眼核；
/// 收到这里之后，<c>UiTextTests</c> 就能把契约钉住，改文案会立刻被发现。
/// 同时 XAML 里不允许出现中文文案（架构 §6.5），结构上防止"界面悄悄多出一句话"。
///
/// 边界（架构 §3.9 定稿）：本类只收 <b>BCL 原语</b>，<b>绝不</b>出现
/// <c>PlanItem</c> / <c>TransferReport</c> / <c>TransferEngine</c> 等 Engine 具体类型。
/// 分工是："<b>说什么话、走哪个分支</b>"在本类；"<b>取哪些字段</b>"在 Engine，各一行转调
/// （<c>PlanItem.Describe()</c> / <c>TransferReport.Summary()</c>）。
/// 这样既不会让"失败为 0 则整段省略"这类文案规则漏到 Engine 里，也不会形成
/// <c>Engine → UiText → Engine</c> 的类型环。
/// </remarks>
public static class UiText
{
    // ==================================================================== 窗口与列标题

    /// <summary>窗口标题（由系统标题栏承担，正文不重复）。</summary>
    public const string WindowTitle = "DirMover — 用户数据转移工具";

    /// <summary>「文件夹名称」列标题。</summary>
    public const string ColumnName = "文件夹名称";

    /// <summary>「当前位置」列标题。</summary>
    public const string ColumnCurrent = "当前位置";

    /// <summary>「转移目标」列标题。</summary>
    public const string ColumnTarget = "转移目标";

    /// <summary>「状态」列标题（该列不裁字，宽度受列宽算法保护）。</summary>
    public const string ColumnStatus = "状态";

    /// <summary>目标根目录标签。</summary>
    public const string DestinationLabel = "目标根目录";

    /// <summary>表格列表的无障碍名称（读屏在进入列表时播报，如「用户目录列表，列表」）。</summary>
    public const string FolderListLabel = "用户目录列表";

    // ==================================================================== 按钮与勾选框

    /// <summary>主操作按钮：开始转移。</summary>
    public const string ButtonStart = "开始转移";

    /// <summary>主操作按钮（执行中）：停止。</summary>
    public const string ButtonStop = "停止";

    /// <summary>主操作按钮（停止中，同时禁用）。</summary>
    public const string ButtonStopping = "正在停止…";

    /// <summary>主操作按钮（恢复系统默认值期间，同时禁用）。</summary>
    public const string ButtonRestoring = "恢复中…";

    /// <summary>主操作按钮（重启资源管理器期间，同时禁用）。</summary>
    public const string ButtonRestarting = "重启中…";

    /// <summary>危险操作按钮：恢复系统默认值。</summary>
    public const string ButtonRestoreDefaults = "恢复系统默认值";

    /// <summary>退出程序按钮。</summary>
    public const string ButtonExit = "退出程序";

    /// <summary>状态栏链接：打开日志目录。</summary>
    public const string ButtonOpenLog = "打开日志目录";

    /// <summary>提权兜底链接：以管理员身份重启。</summary>
    public const string ButtonRelaunchAsAdmin = "以管理员身份重启";

    /// <summary>全选勾选框。</summary>
    public const string CheckBoxSelectAll = "全选/全不选";

    /// <summary>创建当前用户目录勾选框。</summary>
    public const string CheckBoxCreateUserDirectory = "创建当前用户目录";

    // ==================================================================== 通用按钮文案（弹窗用）

    /// <summary>「是」（Primary）。</summary>
    public const string ButtonYes = "是";

    /// <summary>「否」（Secondary 或 Close）。</summary>
    public const string ButtonNo = "否";

    /// <summary>「取消」（三选弹窗的 Close）。</summary>
    public const string ButtonCancel = "取消";

    /// <summary>「确定」（单按钮弹窗）。</summary>
    public const string ButtonOk = "确定";

    // ==================================================================== 状态栏文案

    /// <summary>空闲状态。</summary>
    public const string Ready = "就绪";

    /// <summary>无可用目标盘时的简短文案（空间标签位置）。</summary>
    public const string NoTargetDisk = "未检测到可用的非系统固定磁盘";

    /// <summary>无可用目标盘时的完整提示。</summary>
    public const string NoTargetDiskHint = "未检测到可用的目标磁盘（非系统固定盘），请先接入或挂载。";

    /// <summary>停止中：必须说明已完成项保留、未完成项源文件不删。</summary>
    public const string Stopping =
        "正在停止：已完成的项目会保留；未完成项目的源文件不会被删除，注册表也不会写入。";

    /// <summary>恢复中。</summary>
    public const string Restoring = "正在恢复系统默认位置…";

    /// <summary>重启资源管理器期间。</summary>
    public const string RestartingShell = "重启中…";

    /// <summary>关窗收尾期间。</summary>
    public const string SafeShutdown = "正在安全收尾：等当前项写完并校验后自动退出…";

    /// <summary>试运行徽标。</summary>
    public const string DryRunBadge = "试运行模式";

    /// <summary>界面回调失败（唯一允许吞掉的 UI 异常，归架构 §6.2 第 ⑨ 条）。</summary>
    public const string UiRefreshFailed = "界面刷新失败，详情见日志文件";

    // ==================================================================== 状态列（PRD §6.1，先命中先返回）

    /// <summary>当前位置 == 目标。</summary>
    /// <remarks>
    /// 2026-11-14 按用户反馈由「路径相同，无需转移」缩短为「无需转移」：
    /// 界面常态下该文案连续重复 7 次，整句解释是视觉噪音；
    /// 行底色/状态圆点/空态徽章已把"为什么"讲清楚，短句足够。
    /// </remarks>
    public const string StatusSame = "无需转移";

    /// <summary>恢复默认值且源目录已不存在（只修注册表）。</summary>
    public const string StatusRegistryOnly = "仅更新注册表";

    /// <summary>尚未扫描规模（界面常态）。</summary>
    public const string StatusPending = "待转移";

    /// <summary>规模在执行时统计（确认框用）。</summary>
    public const string ScaleUnavailable = "（规模在执行时统计）";

    // ==================================================================== 失败文案（PRD §6.4，必须给出下一步）

    /// <summary>空选提示（主按钮禁用原因 / 零选时点击主按钮的提示）。</summary>
    public const string NoSelectionWarning = "请先勾选至少一个项目。";

    /// <summary>摘要里的零选追加提示。</summary>
    public const string NoSelectionHint = "（请先勾选项目）";

    /// <summary>「转移目标」列在目标＝当前位置时的占位文案（弱化显示，避免两列重复同一路径）。</summary>
    /// <remarks>
    /// 2026-11-14 按用户反馈去掉破折号前缀：破折号悬在空列里反而突兀，
    /// 弱化灰字本身已足够表达"这一列不用看"。
    /// </remarks>
    public const string SameTargetPlaceholder = "同当前位置";

    /// <summary>表格空态提示主标题：所有目录都已在目标盘时显示（圆形徽章下方的强文字）。</summary>
    public const string EmptyStateAllSameTitle = "一切就绪，无需转移";

    /// <summary>表格空态提示副文案：主标题下方的弱化说明，指引下一步动作。</summary>
    /// <remarks>
    /// 2026-11-14 按用户反馈收紧："所有目录都已在目标盘"与主标题及状态列 7 行
    /// "无需转移"三重重复，删掉后只留唯一的增量信息——下一步做什么。
    /// </remarks>
    public const string EmptyStateAllSameSubtitle = "换一个目标盘即可开始转移";

    /// <summary>上次事务记录不可用时的结论句。</summary>
    public const string PendingUnreadableAction =
        "无法判断上次是否有半完成的项目，也无法一键回滚，请人工核对当前位置与目录内容。";

    /// <summary>结果里"注册表备份未能落盘"的附加提示（PRD §6.3 的附加提示段）。</summary>
    public const string SnapshotNotSaved = "注册表备份未能落盘，本次无法一键回滚";

    // ==================================================================== 结果标题

    /// <summary>全部成功的结果标题。</summary>
    public const string ResultOk = "操作完成";

    /// <summary>有失败项但还有成功项的结果标题。</summary>
    public const string ResultPartial = "部分项目未完成";

    /// <summary>被取消的结果标题。</summary>
    public const string ResultStopped = "已停止";

    /// <summary>没有成功项且失败的结果标题。</summary>
    public const string ResultError = "执行出错";

    /// <summary>
    /// 结果弹窗标题：操作完成 / 部分项目未完成 / 已停止 / 执行出错。
    /// </summary>
    /// <param name="failed">是否存在失败项。</param>
    /// <param name="cancelled">是否被用户取消。</param>
    /// <param name="anyOk">是否存在成功项。</param>
    /// <remarks>
    /// 判定顺序固定：有失败项先看"是否还有成功项"（决定是"部分未完成"还是"执行出错"），
    /// 再看取消。反过来（先看取消）会把"失败 + 用户中途停止"报成"已停止"，掩盖真实失败。
    /// </remarks>
    public static string ResultTitle(bool failed, bool cancelled, bool anyOk)
    {
        if (failed)
        {
            return anyOk ? ResultPartial : ResultError;
        }

        return cancelled ? ResultStopped : ResultOk;
    }

    // ==================================================================== 弹窗标题

    /// <summary>「无法开始」弹窗标题。</summary>
    public const string DialogCannotStart = "无法开始";

    /// <summary>「无需转移」弹窗标题。</summary>
    public const string DialogNoMove = "无需转移";

    /// <summary>「确认开始转移」弹窗标题。</summary>
    public const string DialogConfirmTransfer = "确认开始转移";

    /// <summary>「确认恢复系统默认值」弹窗标题。</summary>
    public const string DialogConfirmRestore = "确认恢复系统默认值";

    /// <summary>「正在执行」弹窗标题（退出确认）。</summary>
    public const string DialogBusy = "正在执行";

    /// <summary>「重启资源管理器」弹窗标题。</summary>
    public const string DialogRestartShell = "重启资源管理器";

    /// <summary>「上次转移未正常收尾」弹窗标题（三选）。</summary>
    public const string DialogPendingIntent = "上次转移未正常收尾";

    /// <summary>「上次事务记录不可用」弹窗标题（单按钮警示）。</summary>
    public const string DialogPendingUnreadable = "上次事务记录不可用";

    /// <summary>回滚成功标题。</summary>
    public const string DialogRollbackOk = "回滚完成";

    /// <summary>回滚失败标题。</summary>
    public const string DialogRollbackFailed = "回滚失败";

    /// <summary>提权提示标题。</summary>
    public const string DialogElevation = "需要管理员权限";

    /// <summary>重复启动标题。</summary>
    public const string DialogAlreadyRunning = "程序已在运行";

    /// <summary>未选择项目标题。</summary>
    public const string DialogNoSelection = "未选择项目";

    /// <summary>只读扫描报告弹窗的标题。</summary>
    /// <remarks>
    /// 2026-11-14 由 <see cref="WindowTitle"/> 改为专名：报告正文首行已含"DirMover 扫描报告"，
    /// 标题再用窗口全名就是同一句话出现两遍；短标题也让弹窗标题栏与窗口标题栏不再雷同。
    /// </remarks>
    public const string DialogScanReportTitle = "扫描报告";

    /// <summary>未预期错误的告知标题（同一进程只在第一次出现时弹一次）。</summary>
    public const string DialogUnexpectedError = "程序遇到未预期的错误";

    /// <summary>未预期错误的正文（说明已记录、程序会继续、以及下一步该做什么）。</summary>
    public const string UnexpectedErrorBody =
        "刚才的操作出现了未预期的错误，已写入启动错误文件与日志。程序会继续运行，" +
        "但界面可能已经不完整；建议保存好手头的事后重新打开程序，" +
        "并用状态栏的「打开日志目录」把日志提供给维护者。若错误再次出现，程序会直接退出并返回退出码 4。";

    // ==================================================================== 弹窗正文

    /// <summary>无需转移的正文。</summary>
    public const string NoMoveBody = "选中的项目目标路径与当前位置相同，没有需要转移的内容。";

    /// <summary>重复启动的正文。</summary>
    public const string AlreadyRunningBody = "程序已在运行。请勿重复打开。";

    /// <summary>被取消（已停止）的正文。</summary>
    public const string StoppedBody =
        "已按你的要求停止。已完成的项目保留；未完成项目的源文件没有被删除，注册表也没有写入。";

    /// <summary>全部成功的正文。</summary>
    public const string DoneBody = "所有选中项目已处理完成。";

    /// <summary>询问重启资源管理器的正文。</summary>
    public const string AskRestartShellBody =
        "注册表已更新。是否重启资源管理器让改动立即生效？（重启期间桌面会短暂闪一下）";

    /// <summary>提权提示的正文。</summary>
    public const string ElevationBody =
        "当前未以管理员身份运行，迁移会在写注册表或删除目录时失败。建议以管理员身份重新启动。";

    /// <summary>失败项清单为空时的兜底说明。</summary>
    public const string FailureListEmpty = "（没有可列出的失败明细，请查看日志文件）";

    // ==================================================================== 格式化方法

    /// <summary>
    /// 已选摘要：<c>已选择 N / 7 项</c>；零选时追加 <c>（请先勾选项目）</c>。
    /// </summary>
    /// <param name="selected">已选数量。</param>
    /// <param name="total">总数量（固定 7，由调用方从 <see cref="Catalog.All"/> 取）。</param>
    public static string SelectionSummary(int selected, int total)
    {
        int safeSelected = Math.Max(0, selected);
        int safeTotal = Math.Max(0, total);
        string text = $"已选择 {safeSelected} / {safeTotal} 项";
        return safeSelected == 0 ? text + NoSelectionHint : text;
    }

    /// <summary>
    /// 带「无需转移」分支的已选摘要：把"能搬的"和"已经在目标盘的"分开计数。
    /// </summary>
    /// <param name="actionableSelected">已勾选的<b>可转移</b>项数。</param>
    /// <param name="actionableTotal">可转移项总数（位置与目标不同的行）。</param>
    /// <param name="notNeeded">无需转移项数（当前位置＝目标位置）。</param>
    /// <remarks>
    /// 为什么不用单一计数：旧摘要「已选择 7 / 7 项」在"七项都无需转移"时是误导——
    /// 看起来有 7 项待办，实际一项都不会动。拆成两个数字后，摘要与主按钮的可用性才对得上。
    /// </remarks>
    public static string SelectionSummaryDetail(int actionableSelected, int actionableTotal, int notNeeded)
    {
        int selected = Math.Max(0, actionableSelected);
        int total = Math.Max(0, actionableTotal);
        int skipped = Math.Max(0, notNeeded);

        if (total == 0)
        {
            return skipped == 0 ? SelectionSummary(0, 0) : $"全部 {skipped} 项无需转移";
        }

        if (skipped == 0)
        {
            return SelectionSummary(selected, total);
        }

        string text = $"可转移 {selected} / {total} 项 · 无需转移 {skipped} 项";
        return selected == 0 ? text + NoSelectionHint : text;
    }

    /// <summary>
    /// 执行中状态栏：<c>正在处理 {显示名}（{done}/{total}）</c>。
    /// </summary>
    /// <param name="display">当前项的显示名。</param>
    /// <param name="done">已完成项数。</param>
    /// <param name="total">总项数。</param>
    public static string Processing(string display, int done, int total) =>
        $"正在处理 {display}（{done}/{total}）";

    /// <summary>未扫描规模时的规模文案（沿用原实现的既有显示，不"顺手修正"）。</summary>
    public const string EmptyTreeStatText = "0 个文件 / 0 B";

    /// <summary>
    /// 状态列文案的<b>唯一实现</b>（PRD §6.1 五个分支，先命中先返回）。
    /// </summary>
    /// <param name="same">当前位置与目标是否相同。</param>
    /// <param name="registryOnly">是否"仅更新注册表"（恢复路径下源目录已不存在）。</param>
    /// <param name="merge">目标是否已存在且非空（将合并）。</param>
    /// <param name="scanned">是否已扫描过规模。</param>
    /// <param name="statText">规模文案（<see cref="TreeStat.Describe"/>）；空串按 <see cref="EmptyTreeStatText"/> 处理。</param>
    /// <remarks>
    /// 分工固定：<b>说什么话、走哪个分支</b>在 UiText，<b>取哪些字段</b>在 Engine（各一行转调）。
    /// 下面三个公开重载全部收敛到这里——文案一旦有两份实现，改文案时必然漏改一处。
    /// </remarks>
    internal static string DescribePlanCore(
        bool same,
        bool registryOnly,
        bool merge,
        bool scanned,
        string statText)
    {
        string scale = string.IsNullOrEmpty(statText) ? EmptyTreeStatText : statText;

        if (same)
        {
            return StatusSame;
        }

        if (registryOnly)
        {
            return StatusRegistryOnly;
        }

        // 注意：未扫描时规模显示 `0 个文件 / 0 B` 是原实现的既有行为，不在重写里"顺手修正"
        // —— 修正会改变界面常态文案，属行为偏差。
        if (merge)
        {
            return $"将合并（{scale}）";
        }

        if (!scanned)
        {
            return StatusPending;
        }

        return $"待转移，{scale}";
    }

    /// <summary>
    /// 状态列文案（<see cref="TreeStat"/> 形态）。
    /// </summary>
    /// <param name="same">当前位置与目标是否相同。</param>
    /// <param name="registryOnly">是否"仅更新注册表"。</param>
    /// <param name="merge">是否合并。</param>
    /// <param name="scanned">是否已扫描过规模。</param>
    /// <param name="stat">规模统计；<c>null</c> 或空统计显示 <c>0 个文件 / 0 B</c>。</param>
    public static string DescribePlan(
        bool same,
        bool registryOnly,
        bool merge,
        bool scanned,
        TreeStat stat) =>
        DescribePlanCore(same, registryOnly, merge, scanned, stat?.Describe() ?? string.Empty);

    /// <summary>
    /// 状态列文案（原始数值形态；保留给只拿到计数、没有 <see cref="TreeStat"/> 的调用点）。
    /// </summary>
    /// <param name="same">当前位置与目标是否相同。</param>
    /// <param name="registryOnly">是否"仅更新注册表"。</param>
    /// <param name="merge">是否合并。</param>
    /// <param name="scanned">是否已扫描过规模。</param>
    /// <param name="files">已扫描到的文件数（未扫描时为 0）。</param>
    /// <param name="bytes">已扫描到的字节数（未扫描时为 0）。</param>
    public static string DescribePlan(
        bool same,
        bool registryOnly,
        bool merge,
        bool scanned,
        int files,
        long bytes) =>
        DescribePlanCore(same, registryOnly, merge, scanned, $"{files} 个文件 / {PathTools.HumanSize(bytes)}");

    /// <summary>
    /// 状态列文案（引擎对象形态：只做字段搬运，分支逻辑在 <see cref="DescribePlanCore"/>）。
    /// </summary>
    /// <param name="item">规划项。</param>
    public static string DescribePlan(PlanItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return DescribePlanCore(item.Same, item.RegistryOnly, item.Merge, item.Scanned, item.Stat.Describe());
    }

    /// <summary>单项结论的短标签：完成 / 失败 / 跳过 / 已取消 / 待处理。</summary>
    /// <param name="status">结论。</param>
    public static string StatusLabel(ItemStatus status) => status switch
    {
        ItemStatus.Ok => "完成",
        ItemStatus.Failed => "失败",
        ItemStatus.Skipped => "跳过",
        ItemStatus.Cancelled => "已取消",
        _ => "待处理",
    };

    /// <summary>
    /// 阶段标签（11 种，供意图日志弹窗逐项展示；PRD §4.3-3 固定文案）。
    /// </summary>
    /// <param name="stage">阶段。</param>
    public static string StageLabel(TransferStage stage) => stage switch
    {
        TransferStage.Pending => "未开始",
        TransferStage.Copying => "正在复制（目标可能只写入一部分）",
        TransferStage.Copied => "复制校验完成，源目录未删除",
        TransferStage.RegistryWritten => "注册表已更新，源目录未删除",
        TransferStage.SourceRemoved => "源目录已删除",
        TransferStage.Cancelled => "已取消",
        TransferStage.CopyFailed => "复制失败",
        TransferStage.VerifyFailed => "复制校验未通过",
        TransferStage.RegistryFailed => "注册表写入失败",
        TransferStage.SourceRemovalFailed => "源目录删除失败",
        _ => "发生未预期错误",
    };

    /// <summary>
    /// 结果摘要：<c>完成 N 项[，失败 N 项][，跳过 N 项][，用户取消][（附加提示）]</c>。
    /// </summary>
    /// <param name="completed">完成项数。</param>
    /// <param name="failed">失败项数（0 时整段省略）。</param>
    /// <param name="skipped">跳过项数（0 时整段省略）。</param>
    /// <param name="cancelled">是否被用户取消。</param>
    /// <param name="notes">附加提示（如"注册表备份未能落盘"）；多条用「；」连接。</param>
    public static string Summarize(
        int completed,
        int failed,
        int skipped,
        bool cancelled,
        IReadOnlyList<string>? notes = null)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("完成 ").Append(Math.Max(0, completed)).Append(" 项");

        if (failed > 0)
        {
            builder.Append("，失败 ").Append(failed).Append(" 项");
        }

        if (skipped > 0)
        {
            builder.Append("，跳过 ").Append(skipped).Append(" 项");
        }

        if (cancelled)
        {
            builder.Append("，用户取消");
        }

        if (notes is { Count: > 0 })
        {
            var usable = new List<string>(notes.Count);
            foreach (string note in notes)
            {
                if (!string.IsNullOrWhiteSpace(note))
                {
                    usable.Add(note);
                }
            }

            if (usable.Count > 0)
            {
                builder.Append('（').Append(string.Join("；", usable)).Append('）');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 结果摘要（引擎对象形态：只做计数搬运，格式化逻辑在
    /// <see cref="Summarize(int, int, int, bool, IReadOnlyList{string}?)"/> 里，只有一份）。
    /// </summary>
    /// <param name="report">执行报告。</param>
    /// <remarks>
    /// 计数口径：<c>完成</c> 只算 <see cref="ItemStatus.Ok"/>；
    /// <c>跳过</c>（路径相同 / 仅更新注册表）单列，<b>不</b>混进完成数——否则
    /// "已完成 3 项"里可能一件都没搬，用户会误判结果。
    /// </remarks>
    public static string Summarize(TransferReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Summarize(
            report.Succeeded.Count,
            report.Failed.Count,
            report.SkippedCount,
            report.Cancelled,
            report.Notes);
    }

    /// <summary>打开日志目录成功：<c>已打开日志目录：{path}</c>。</summary>
    /// <param name="path">实际落点（注意不是"期望落点"，便携/回退两种形态不同）。</param>
    public static string OpenLogOk(string path) => $"已打开日志目录：{path}";

    /// <summary>打开日志目录失败：<c>日志目录不可用（可能没有写权限）：{path}</c>。</summary>
    /// <param name="path">实际落点。</param>
    public static string OpenLogFailed(string path) => $"日志目录不可用（可能没有写权限）：{path}";

    /// <summary>用资源管理器打开目录失败（目录不存在 / 外壳不可用）。</summary>
    /// <param name="path">目录路径。</param>
    public static string OpenDirectoryMissing(string path) => $"无法打开目录（不存在或无权限）：{path}";

    /// <summary>UAC 提权被用户取消。</summary>
    public const string ElevationCancelled = "已取消提权，程序未重启。";

    /// <summary>提权重启失败（含错误码，便于排障）。</summary>
    /// <param name="errorCode">Win32 错误码（0 表示拿不到 exe 路径）。</param>
    public static string ElevationFailed(int errorCode) =>
        errorCode == 0
            ? "无法以管理员身份重启：取不到程序路径。"
            : $"无法以管理员身份重启（错误码 {errorCode}），请手动右键「以管理员身份运行」。";

    /// <summary>资源管理器重启成功。</summary>
    public const string ShellRestartOk = "资源管理器已重启，改动已生效。";

    /// <summary>自动拉起资源管理器失败：必须明说让用户手动运行。</summary>
    public const string ShellRestartManual = "已结束资源管理器但无法自动拉起，请手动运行 explorer.exe。";

    /// <summary>重启资源管理器被取消。</summary>
    public const string ShellRestartCancelled = "已取消重启资源管理器。";

    /// <summary>
    /// 结束 explorer 的命令返回非零退出码（命令本身失败，不能当"已重启"）。
    /// </summary>
    /// <param name="exitCode"><c>taskkill</c> 退出码（-1 = 启动失败或超时）。</param>
    public static string ShellRestartKillFailed(int exitCode) =>
        $"结束资源管理器失败（taskkill 退出码 {exitCode}），改动可能尚未生效；请在任务管理器手动结束 explorer.exe 后运行 explorer.exe，或注销后重新登录。";

    /// <summary>
    /// 已发出结束命令，但结束前的 explorer 进程仍在运行（过滤条件没匹配到 / 进程拒绝退出）。
    /// 必须给出可操作提示，绝不能报"重启成功"。
    /// </summary>
    public const string ShellRestartKillNotConfirmed =
        "已发出结束命令，但本会话的资源管理器仍在运行，重启流程未完成；请在任务管理器手动结束 explorer.exe 后运行 explorer.exe，或注销后重新登录。";

    /// <summary>
    /// 取不到当前会话号：不能发出无法按会话收窄的结束命令（会误伤其它会话的桌面）。
    /// </summary>
    public const string ShellRestartSessionUnknown =
        "无法确定当前会话，为避免结束其它会话的桌面，本次未重启资源管理器；请手动运行 explorer.exe 或注销后重新登录。";

    // ---------------------------------------------------------------- 失败文案族（含去向 + 下一步）

    /// <summary>当前位置无法确定（注册表里既没有权威值也没有别名）。</summary>
    public static string CurrentUnknown() => "无法确定当前位置（注册表里找不到该文件夹的值），请先在资源管理器里打开一次该文件夹后重试";

    /// <summary>当前位置不存在（注册表指向的目录已不在）。</summary>
    /// <param name="path">当前位置。</param>
    public static string CurrentMissing(string path) => $"当前位置不存在：{path}";

    /// <summary>规划期读注册表失败（fail-closed：不知道当前位置就不动手）。</summary>
    /// <param name="reason">底层原因。</param>
    public static string RegistryReadFailed(string reason) =>
        $"读取注册表失败，无法确认当前位置：{reason}";

    /// <summary>单项被取消（源目录未删除、注册表未写入）。</summary>
    public const string CancelledItem = "已取消：源目录未删除，注册表未写入";

    /// <summary>写注册表之后被取消（源目录保留，属于半完成状态）。</summary>
    /// <param name="current">源目录。</param>
    public static string CancelledAfterRegistry(string current) =>
        $"已取消：注册表已指向新位置，源目录保留在 {current}（下次启动会再次提示）";

    /// <summary>清理目录失败：说明原因并给出唯一可靠出路（重启后手动删除）。</summary>
    /// <param name="reason">失败原因（多为占用）。</param>
    /// <param name="path">目录路径。</param>
    public static string RemoveDirectoryFailed(string reason, string path) =>
        $"清理目录失败：{reason}；重启本机后即可删除（{path}）";

    /// <summary>复制失败：源目录已保留，并指出目标副本位置。</summary>
    /// <param name="reason">失败原因。</param>
    /// <param name="target">目标路径。</param>
    public static string CopyFailed(string reason, string target) =>
        $"复制失败：{reason}；源目录已保留，目标副本位于 {target}，修正后重试即可";

    /// <summary>校验未通过：明确"已保留源目录"。</summary>
    /// <param name="detail">校验失败的具体维度与样例。</param>
    public static string VerifyFailed(string detail) => $"复制校验未通过，已保留源目录（{detail}）";

    /// <summary>写注册表失败：给出子键\值名、原因、就地回滚数量。</summary>
    /// <param name="subKey">子键（相对 HKCU）。</param>
    /// <param name="name">值名。</param>
    /// <param name="reason">失败原因。</param>
    /// <param name="rolledBack">本次已回滚的注册表值处数。</param>
    public static string RegistryWriteFailed(string subKey, string name, string reason, int rolledBack) =>
        $"写入注册表失败 {subKey}\\{name}：{reason}；已回滚本项 {rolledBack} 处注册表值";

    /// <summary>复制路径下的补句：源目录已保留，并指出目标副本位置。</summary>
    /// <param name="target">目标路径。</param>
    public static string SourceKeptAtTarget(string target) =>
        $"源目录已保留，目标副本位于 {target}";

    /// <summary>rename 路径下写注册表失败后的"目录已改回原位"说明。</summary>
    /// <param name="target">目标路径（现已不存在）。</param>
    /// <param name="current">原位置。</param>
    public static string MovedBackToCurrent(string target, string current) =>
        $"已把 {target} 改回 {current}";

    /// <summary>rename 路径下写注册表失败且无法改回时的"数据实际位置"说明。</summary>
    /// <param name="target">数据实际所在路径。</param>
    public static string DataAtTargetNoRegistryChange(string target) =>
        $"数据已在 {target}，注册表未改动，请勿重复搬迁";

    /// <summary>删源失败：数据安全，重启后手动删除即可。</summary>
    /// <param name="reason">失败原因（多为句柄占用）。</param>
    /// <param name="current">源目录。</param>
    public static string SourceRemoveFailed(string reason, string current) =>
        $"数据已完整复制并校验通过，但清理源目录失败：{reason}（源目录：{current}）；重启本机后手动删除即可";

    /// <summary>空间不足（只阻断该项）。</summary>
    /// <param name="needed">内容体积。</param>
    /// <param name="recommended">建议预留（含余量）。</param>
    /// <param name="available">目标卷可用空间。</param>
    public static string SpaceInsufficient(string needed, string recommended, string available) =>
        $"目标磁盘空间不足：内容 {needed}，建议预留 {recommended}，可用 {available}";

    /// <summary>空间未知（读不到可用空间即阻断，不得当成"无需检查"）。</summary>
    public const string SpaceUnknown = "无法读取目标磁盘可用空间，为避免写满磁盘已停止";

    /// <summary>统计期存在不可读项（规模被低估，不能据此判断空间充足）。</summary>
    /// <param name="count">不可读项数量。</param>
    public static string UnreadableItems(int count) =>
        $"有 {count} 项内容无法读取（被占用或无权限），无法确认能否安全转移";

    /// <summary>目标或其上级成了链接/联接点。</summary>
    /// <param name="path">命中的重解析点路径。</param>
    public static string TargetIsLink(string path) =>
        $"目标路径或其上级目录成了链接/联接点（{path}）：数据会被写到链接指向的位置，已停止该项（源目录未改动）";

    /// <summary>源与目标实际指向同一物理位置。</summary>
    /// <param name="current">源目录。</param>
    /// <param name="target">目标目录。</param>
    public static string SamePhysicalLocation(string current, string target) =>
        $"源与目标实际指向同一位置，为安全起见保留源目录（{current}）；注册表已指向 {target}，如需回退可用备份快照回滚";

    /// <summary>源在复制期间发生变化。</summary>
    /// <param name="added">新增项数。</param>
    /// <param name="removed">减少项数。</param>
    /// <param name="modified">被改写项数。</param>
    /// <param name="current">源目录。</param>
    /// <param name="target">目标目录。</param>
    public static string SourceChangedDuringCopy(
        int added,
        int removed,
        int modified,
        string current,
        string target) =>
        $"源目录在复制期间发生变化（新增 {added} 项/少了 {removed} 项/{modified} 项被改写），为安全起见保留源目录：{current}；注册表已指向 {target}，如需回退可用备份快照回滚";

    /// <summary>删源前复检发现目标或其上级变成了链接/联接点（源目录保留）。</summary>
    /// <param name="path">命中的重解析点路径。</param>
    /// <param name="current">源目录。</param>
    /// <remarks>
    /// 与 <see cref="TargetIsLink"/> 分开写：那句是"还没动文件就被拦下"，本句是"数据已经复制、
    /// 注册表已经写过之后才发现目标被换成链接"，用户需要知道的两件事完全不同（后者要说明
    /// 注册表已指向新位置、以及怎么回退）。混用会让排查时看不出是哪一道闸门拦下的。
    /// </remarks>
    public static string TargetBecameLinkBeforeRemove(string path, string current) =>
        $"删源前复检发现目标路径或其上级变成了链接/联接点（{path}）：为安全起见保留源目录 {current}；注册表已指向该链接位置，请人工核对目标，如需回退可用备份快照回滚";

    /// <summary>复制失败但错误清单为空时的兜底原因（不该发生，但不得留空白文案）。</summary>
    public const string UnknownReason = "未知原因";

    /// <summary>试运行单项结论的前缀。</summary>
    /// <param name="message">原始说明。</param>
    public static string DryRunOutcome(string message) => $"[试运行] {message}";

    // ---------------------------------------------------------------- 弹窗正文装配

    /// <summary>
    /// 确认开始转移的正文：项数 + 规模（或"执行时统计"）+ 目标根 + 安全说明。
    /// </summary>
    /// <param name="count">将转移的项数。</param>
    /// <param name="scaleSummary">规模摘要（可为空 → 显示"规模在执行时统计"）。</param>
    /// <param name="targetRoot">目标根目录。</param>
    public static string ConfirmTransferBody(int count, string? scaleSummary, string targetRoot)
    {
        // net48 引用程序集缺 NotNullWhen(false)：IsNullOrWhiteSpace 不收窄参数，
        // false 分支需要显式 !（net8 侧本就干净，加 ! 无行为影响）。
        string scale = string.IsNullOrWhiteSpace(scaleSummary) ? ScaleUnavailable : scaleSummary!;
        return $"即将转移 {count} 项{scale}。" + Environment.NewLine +
               $"目标根目录：{targetRoot}" + Environment.NewLine +
               "迁移过程会先复制并逐文件校验，校验通过后才更新注册表并删除源目录；" +
               "任何一步失败都会保留源文件。";
    }

    /// <summary>
    /// 确认恢复系统默认值的正文：逐项列出显示名 + 恢复语义说明。
    /// </summary>
    /// <param name="displays">将恢复的项显示名（按规格表顺序）。</param>
    public static string ConfirmRestoreBody(IReadOnlyList<string> displays)
    {
        var lines = new List<string> { "将把下列项目恢复为系统默认位置：" };
        if (displays is { Count: > 0 })
        {
            foreach (string display in displays)
            {
                lines.Add("· " + display);
            }
        }

        lines.Add("内容会搬回系统默认目录，注册表也会改回指向该目录；先复制校验，全部通过后才写注册表、删原目录。");
        lines.Add("任一步失败都不会删源、不改注册表；空间不足的项会单独跳过并列出缺口。");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 执行中退出确认的正文。可中断任务建议先停止；不可中断任务说明会等它完成。
    /// </summary>
    /// <param name="cancelable">当前任务是否可中断。</param>
    public static string ConfirmExitBody(bool cancelable) => cancelable
        ? "当前正在执行转移。建议先点主按钮上的「停止」，让当前项安全收尾后再退出。" +
          Environment.NewLine + "仍然退出程序吗？（会先安全收尾，可能需要一点时间）"
        : "当前步骤没有停止入口，退出会等它完成（最长约 20 秒，含两段等待合计约 22 秒）后自动收尾。" +
          Environment.NewLine + "仍然退出程序吗？";

    /// <summary>意图日志单项行：<c>· 显示名：阶段标签 / 源：… / 目标：…</c>。</summary>
    /// <param name="display">显示名。</param>
    /// <param name="stage">阶段。</param>
    /// <param name="source">源路径。</param>
    /// <param name="target">目标路径。</param>
    public static string PendingIntentLine(string display, TransferStage stage, string source, string target) =>
        $"· {display}：{StageLabel(stage)}" + Environment.NewLine +
        $"  源：{source}" + Environment.NewLine +
        $"  目标：{target}";

    /// <summary>意图日志弹窗正文：说明 + 逐项行 + 三选语义。</summary>
    /// <param name="lines">由 <see cref="PendingIntentLine"/> 生成的行。</param>
    public static string PendingIntentBody(IReadOnlyList<string> lines)
    {
        var body = new List<string>
        {
            "上次转移没有正常收尾，以下项目可能处于半完成状态：",
        };
        if (lines is { Count: > 0 })
        {
            body.AddRange(lines);
        }

        body.Add("选「是」：用备份快照回滚注册表（只还原注册表值，不搬回文件，且只回滚未完成项）。");
        body.Add("选「否」：忽略并删除这条记录。选「取消」：保留记录，下次启动再提示。");
        body.Add("要把文件搬回去，请使用「恢复系统默认值」。");
        return string.Join(Environment.NewLine, body);
    }

    /// <summary>回滚结果：四项计数（恢复 / 删除 / 已跳过属于已完成项目 / 因原值未知跳过）。</summary>
    /// <param name="restored">恢复的处数。</param>
    /// <param name="removed">删除的处数。</param>
    /// <param name="skippedCompleted">因属于已完成项目而跳过的处数。</param>
    /// <param name="unknownSkipped">因快照期读取失败、原值未知而跳过的处数。</param>
    public static string RollbackResult(
        int restored,
        int removed,
        int skippedCompleted,
        int unknownSkipped)
    {
        string text = $"恢复 {Math.Max(0, restored)} 处 / 删除 {Math.Max(0, removed)} 处";
        if (skippedCompleted > 0)
        {
            text += $" / 已跳过 {skippedCompleted} 处属于已完成项目";
        }

        if (unknownSkipped > 0)
        {
            text += $" / 因原值未知跳过 {unknownSkipped} 处";
            text += Environment.NewLine + "其中因原值未知被跳过的条目既不能恢复也不能删除，需要人工核对。";
        }

        return text;
    }

    /// <summary>失败明细截断说明：<c>（共 N 项，以下仅列前 3 条）</c>。</summary>
    /// <param name="total">失败项总数。</param>
    /// <param name="shown">实际列出的条数。</param>
    public static string FailureListTruncated(int total, int shown) =>
        $"（共 {total} 项，以下仅列前 {shown} 条）";

    /// <summary>失败明细的引导句：指引"打开日志目录"看全量。</summary>
    public const string FailureListGuide = "完整过程记录见「打开日志目录」中的日志文件。";

    /// <summary>给定列标签与路径，拼出单元格完整文本提示（F32 降级方案：常显）。</summary>
    /// <param name="columnLabel">列标题（取自本类的列标题常量）。</param>
    /// <param name="path">完整路径。</param>
    public static string PathToolTip(string columnLabel, string path) => $"{columnLabel}：{path}";

    // ---------------------------------------------------------------- 禁区校验文案（DestinationGuard 用）

    /// <summary>目标路径为空。</summary>
    public static string GuardEmptyTarget() => "目标路径不能为空。";

    /// <summary>目标路径含非法字符。</summary>
    /// <param name="character">首个非法字符。</param>
    public static string GuardIllegalCharacter(char character) => $"目标路径含非法字符（{character}）。";

    /// <summary>目标不是绝对路径。</summary>
    public static string GuardNotAbsolute() => @"目标路径需为绝对路径，例如 D:\UserData。";

    /// <summary>路径片段是保留名或以点/空格结尾。</summary>
    /// <param name="segment">出问题的片段。</param>
    public static string GuardInvalidSegment(string segment) =>
        $"路径片段「{segment}」不可用：Windows 保留名，或以点/空格结尾。";

    /// <summary>目标缺少盘符。</summary>
    public static string GuardMissingDrive() => "目标路径缺少盘符。";

    /// <summary>目标磁盘不存在或未就绪。</summary>
    /// <param name="drive">盘符。</param>
    public static string GuardDriveMissing(string drive) => $"目标磁盘 {drive} 不存在或未就绪。";

    /// <summary>目标落在系统目录内。</summary>
    /// <param name="forbidden">命中的禁区根。</param>
    public static string GuardForbiddenSystemDirectory(string forbidden) =>
        $"不能把用户文件夹转移到系统目录 {forbidden}。";

    /// <summary>目标本身就是磁盘根目录（如 <c>C:\</c>）。</summary>
    /// <param name="root">盘根。</param>
    /// <remarks>
    /// 与 <see cref="GuardForbiddenSystemDirectory"/> 分开写：触发的闸门不同（盘根 ≠ 系统目录子树），
    /// 混用同一句会让排查时看不出到底是哪道闸拦下的。
    /// </remarks>
    public static string GuardDriveRootAsTarget(string root) =>
        $"盘根 {root} 不能作为转移目标，请在其下选择具体目录。";

    /// <summary>目标与源实际指向同一物理位置。</summary>
    /// <param name="target">目标路径。</param>
    /// <param name="source">源路径。</param>
    public static string GuardSamePhysical(string target, string source) =>
        $"目标与待转移目录实际指向同一位置（链接或盘符别名）：{target}（{source}）";

    /// <summary>两个待转移项算出了重叠的目标路径。</summary>
    /// <param name="first">第一个文件夹的界面名。</param>
    /// <param name="second">第二个文件夹的界面名。</param>
    /// <remarks>
    /// 指向用户可操作的动作（改显示名或分开转移），因为这一条通常源于
    /// desktop.ini 里自定义过的显示名，而不是用户填错了什么。
    /// </remarks>
    public static string TargetOverlap(string first, string second) =>
        $"「{first}」与「{second}」的目标路径相同或互相包含，继续执行会把两个文件夹合并成一个；"
        + "请分开转移，或先修正该文件夹的显示名。";

    /// <summary>目标位于源内部。</summary>
    /// <param name="target">目标路径。</param>
    /// <param name="source">源路径。</param>
    public static string GuardTargetInsideSource(string target, string source) =>
        $"目标位于待转移目录内部：{target}（在 {source} 之内）";

    /// <summary>源位于目标内部。</summary>
    /// <param name="source">源路径。</param>
    /// <param name="target">目标路径。</param>
    public static string GuardSourceInsideTarget(string source, string target) =>
        $"待转移目录位于目标内部：{source}（在 {target} 之内）";

    /// <summary>目标自身或任一已存在上级是链接/联接点。</summary>
    /// <param name="path">命中的重解析点。</param>
    public static string GuardTargetIsLink(string path) =>
        $"目标路径或其上级目录是链接/联接点（{path}）：数据会被写到链接指向的位置，请改用真实目录";

    /// <summary>源位置不是合法路径。</summary>
    /// <param name="path">源路径。</param>
    public static string SourceInvalidPath(string path) => $"当前位置不是合法路径（{path}）";

    /// <summary>源位置是磁盘根目录。</summary>
    /// <param name="path">源路径。</param>
    public static string SourceDriveRoot(string path) => $"当前位置是磁盘根目录（{path}）";

    /// <summary>源位置落在系统目录内。</summary>
    /// <param name="root">命中的禁区根。</param>
    public static string SourceInsideSystemDirectory(string root) => $"当前位置在系统目录 {root} 内";

    /// <summary>源位置本身就是系统目录。</summary>
    /// <param name="root">命中的禁区根。</param>
    public static string SourceIsSystemDirectory(string root) => $"当前位置就是系统目录 {root}";

    /// <summary>恢复路径：源不是合法路径。</summary>
    /// <param name="reason">底层原因。</param>
    public static string RestoreRejectedInvalid(string reason) => $"{reason}，拒绝搬回";

    /// <summary>恢复路径：源是磁盘根目录。</summary>
    /// <param name="reason">底层原因。</param>
    public static string RestoreRejectedDriveRoot(string reason) => $"{reason}，不能整体搬回系统默认位置";

    /// <summary>恢复路径：源是系统目录。</summary>
    /// <param name="reason">底层原因。</param>
    public static string RestoreRejectedSystemDirectory(string reason) => $"{reason}，不能搬回";

    /// <summary>恢复路径：默认位置位于源内部（会自我嵌套复制）。</summary>
    /// <param name="target">系统默认位置。</param>
    /// <param name="source">当前位置。</param>
    public static string RestoreTargetInsideSource(string target, string source) =>
        $"系统默认位置 {target} 位于当前位置 {source} 内部，搬回会自我嵌套复制，不能执行";

    /// <summary>恢复路径：源位于默认位置内部（删源会连带删除默认内容）。</summary>
    /// <param name="source">当前位置。</param>
    /// <param name="target">系统默认位置。</param>
    public static string RestoreSourceInsideTarget(string source, string target) =>
        $"当前位置 {source} 位于系统默认位置 {target} 内部，搬回后删源会连带删除默认位置的内容，不能执行";

    // ---------------------------------------------------------------- 四处 ToolTip（仅 4 处，不新增）

    /// <summary>目标根目录下拉的 ToolTip。</summary>
    public const string ToolTipDestination =
        "目标根目录固定为各非系统固定磁盘的 UserData 目录，不支持自由填写。";

    /// <summary>主按钮的 ToolTip。</summary>
    public const string ToolTipPrimary =
        "先复制并逐文件校验，校验通过后才更新注册表并删除源目录。";

    /// <summary>恢复按钮的 ToolTip。</summary>
    public const string ToolTipRestore =
        "把内容搬回系统默认位置，并把注册表改回指向该目录。";

    /// <summary>打开日志目录的 ToolTip。</summary>
    public const string ToolTipOpenLog = "打开日志与备份目录，查看过程详情。";
}
