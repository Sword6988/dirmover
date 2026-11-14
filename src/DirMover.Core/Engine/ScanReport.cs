using DirMover.Core.Diagnostics;
using DirMover.Core.IO;
using DirMover.Core.Registry;

namespace DirMover.Core.Engine;

// ==================================================================== --scan 只读报告
//
// 放在 Engine 而不是 Diagnostics，是**分层**决定而不是风格决定：
// Diagnostics（AppLog / IAppPaths）被启动兜底路径引用，一旦它反向依赖 TransferEngine，
// 启动期崩溃兜底就会把整个引擎拖进加载图；Diagnostics 从此只允许依赖 BCL。
// 文件位置不影响分层，命名空间才影响 —— 所以类型放在 Engine 命名空间，与 TransferEngine 同层。

/// <summary>--scan 报告里的一行（一个用户文件夹）。</summary>
/// <param name="Key">稳定标识。</param>
/// <param name="Display">显示名。</param>
/// <param name="Current">当前位置（注册表解析后已展开）。</param>
/// <param name="Exists">当前位置当前是否存在。</param>
/// <param name="Target">按 <see cref="RunConfig"/> 合成的转移目标。</param>
/// <param name="State">状态（路径相同 / 可执行 / 不可执行：原因）。</param>
/// <param name="RiskLabel">源侧风险标签（正常 / 路径非法 / 盘根 / 系统目录）。</param>
public sealed record ScanReportItem(
    string Key,
    string Display,
    string Current,
    bool Exists,
    string Target,
    string State,
    string RiskLabel);

/// <summary>
/// --scan 的报告数据（只读快照）。<see cref="ToText"/> 负责排版，<see cref="WriteToFile"/> 负责落盘。
/// </summary>
public sealed class ScanReportResult
{
    private const int ItemColumnWidth = 10;
    private const int CurrentColumnWidth = 44;
    private const int TargetColumnWidth = 44;

    /// <summary>生成时间。</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>7 项明细。</summary>
    public IReadOnlyList<ScanReportItem> Items { get; init; } = Array.Empty<ScanReportItem>();

    /// <summary>可用的非系统固定盘。</summary>
    public IReadOnlyList<DriveVolume> Drives { get; init; } = Array.Empty<DriveVolume>();

    /// <summary>权威子键（User Shell Folders）枚举到的值数。</summary>
    public int UserShellFolderValues { get; init; }

    /// <summary>缓存子键（Shell Folders）枚举到的值数。</summary>
    public int ShellFolderValues { get; init; }

    /// <summary>读取告警（注册表读失败、报告被取消等）。空列表 = 全程无告警。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 排成等宽文本（中文按 2 列宽计算，保证中文与 ASCII 混排时不歪）。
    /// </summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            $"DirMover 扫描报告（只读）  {GeneratedAt:yyyy-MM-dd HH:mm:ss}",
            $"版本：{Catalog.AppVersion}    系统盘：{Catalog.SystemDrive}",
            string.Empty,
            "一、7 项用户文件夹",
            "  "
                + Pad("序号", 6)
                + Pad("显示名", ItemColumnWidth)
                + Pad("当前位置", CurrentColumnWidth)
                + Pad("存在", 6)
                + Pad("风险", 10)
                + "状态",
        };

        int index = 1;
        foreach (ScanReportItem item in Items)
        {
            lines.Add(
                "  "
                + Pad(index.ToString(System.Globalization.CultureInfo.InvariantCulture), 6)
                + Pad(item.Display, ItemColumnWidth)
                + Pad(item.Current, CurrentColumnWidth)
                + Pad(item.Exists ? "存在" : "不存在", 6)
                + Pad(item.RiskLabel, 10)
                + item.State);
            index++;
        }

        lines.Add(string.Empty);
        lines.Add("二、可用的非系统固定磁盘");

        if (Drives is { Count: > 0 })
        {
            foreach (DriveVolume drive in Drives)
            {
                lines.Add("  " + Pad(drive.SpaceLabel, TargetColumnWidth + ItemColumnWidth) + "目标根：" + drive.TargetRoot);
            }
        }
        else
        {
            lines.Add("  （无）" + UiText.NoTargetDisk);
        }

        lines.Add(string.Empty);
        lines.Add("三、注册表值计数");
        lines.Add($"   {Catalog.UserShellFoldersSubKey}：{UserShellFolderValues} 个值");
        lines.Add($"   {Catalog.ShellFoldersSubKey}：{ShellFolderValues} 个值");

        lines.Add(string.Empty);
        lines.Add("四、读取告警");
        if (Warnings is { Count: > 0 })
        {
            foreach (string warning in Warnings)
            {
                lines.Add("   · " + warning);
            }
        }
        else
        {
            lines.Add("   无");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 把报告写到指定文件（UTF-8 with BOM，记事本可直接打开）。
    /// </summary>
    /// <param name="path">目标文件路径（父目录会按需创建）。</param>
    /// <returns>是否写成功；失败不抛（由调用方决定如何提示）。</returns>
    public bool WriteToFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                path,
                ToText(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static int DisplayWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int width = 0;
        foreach (char character in text)
        {
            width += IsWide(character) ? 2 : 1;
        }

        return width;
    }

    /// <summary>等宽终端里占两列的字符（CJK / 全角标点 / 谚文）。</summary>
    private static bool IsWide(char character) =>
        (character >= 0x1100 && character <= 0x115F) ||
        (character >= 0x2E80 && character <= 0xA4CF) ||
        (character >= 0xAC00 && character <= 0xD7A3) ||
        (character >= 0xF900 && character <= 0xFAFF) ||
        (character >= 0xFE30 && character <= 0xFE6F) ||
        (character >= 0xFF00 && character <= 0xFF60) ||
        (character >= 0xFFE0 && character <= 0xFFE6);

    private static string Pad(string? text, int width)
    {
        string value = text ?? string.Empty;
        int padding = width - DisplayWidth(value);
        return padding > 0 ? value + new string(' ', padding) : value;
    }
}

/// <summary>
/// <c>--scan</c> 的只读文本报告（裁决 Q1）。
/// </summary>
/// <remarks>
/// 硬约束（评审必查）：<b>零写入</b>，且<b>禁止</b> <c>scan:true</c>。
/// 只允许调 <see cref="TransferEngine.ResolveCurrent"/>、
/// <see cref="TransferEngine.BuildPlan"/>(<c>scan:false</c>) 与只读枚举。
/// 理由：<c>--scan</c> 是"先看清再动手"的入口，如果它自己会写快照/日志/注册表，
/// 或在几十万文件上读盘，用户就不敢在真正迁移前跑它。
/// </remarks>
public static class ScanReport
{
    /// <summary>
    /// 生成报告（CLI 形态：自己枚举盘、自己合成默认配置）。
    /// </summary>
    /// <param name="store">注册表封装（只读使用）。</param>
    /// <param name="ct">取消令牌：在项与项之间检查，取消即停止并记入告警。</param>
    public static Task<ScanReportResult> BuildAsync(RegistryStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        return Task.Run(
            () =>
            {
                IReadOnlyList<DriveVolume> drives = DriveInventory.ListFixedNonSystem();
                RunConfig config = RunConfig.CreateDefault(drives, dryRun: false);
                var engine = new TransferEngine(store, backupDir: null, dryRun: true, NullLog.Instance);
                return BuildResult(engine, config, drives, ct);
            },
            CancellationToken.None);
    }

    /// <summary>
    /// 生成报告数据（App 形态：复用已构造好的引擎与配置）。
    /// </summary>
    /// <param name="engine">引擎（只读使用）。</param>
    /// <param name="config">运行配置（提供目标根与"是否建用户名目录"）。</param>
    /// <param name="drives">已枚举到的非系统固定盘。</param>
    /// <param name="ct">取消令牌。</param>
    public static ScanReportResult BuildResult(
        TransferEngine engine,
        RunConfig config,
        IReadOnlyList<DriveVolume> drives,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(config);

        var warnings = new List<string>();
        var items = new List<ScanReportItem>(Catalog.All.Count);

        // scan:false —— 报告绝不扫盘（几十万文件会把 --scan 变成事故）。
        IReadOnlyList<PlanItem> plan = engine.BuildPlan(
            Catalog.AllKeys,
            config.DestinationRoot,
            config.CreateUserDirectory,
            Environment.UserName,
            scan: false);

        foreach (PlanItem item in plan)
        {
            if (ct.IsCancellationRequested)
            {
                warnings.Add("报告生成被取消：以下明细可能不完整");
                break;
            }

            bool exists = PathTools.Exists(item.Current);
            string blocked = item.BlockingReason();
            string state = item.Same
                ? UiText.StatusSame
                : blocked.Length > 0
                    ? "不可执行：" + blocked
                    : "可执行";

            items.Add(new ScanReportItem(
                item.Key,
                item.Display,
                item.Current,
                exists,
                item.Target,
                state,
                RiskLabelOf(item.Current)));
        }

        string? readError = engine.Store.ConsumeReadError();
        if (!string.IsNullOrWhiteSpace(readError))
        {
            warnings.Add("读取注册表失败：" + readError);
        }

        IReadOnlyDictionary<string, RegistryValue> userShellFolders =
            engine.Store.EnumerateValues(Catalog.UserShellFoldersSubKey);
        IReadOnlyDictionary<string, RegistryValue> shellFolders =
            engine.Store.EnumerateValues(Catalog.ShellFoldersSubKey);

        string? enumerateError = engine.Store.ConsumeReadError();
        if (!string.IsNullOrWhiteSpace(enumerateError))
        {
            warnings.Add("枚举注册表失败：" + enumerateError);
        }

        return new ScanReportResult
        {
            GeneratedAt = DateTimeOffset.Now,
            Items = items,
            Drives = drives ?? Array.Empty<DriveVolume>(),
            UserShellFolderValues = userShellFolders.Count,
            ShellFolderValues = shellFolders.Count,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// 生成报告文本（架构 §3.8 的形态；内部与 <see cref="BuildResult"/> 同一条实现）。
    /// </summary>
    /// <param name="engine">引擎（只读使用）。</param>
    /// <param name="config">运行配置。</param>
    /// <param name="drives">已枚举到的非系统固定盘。</param>
    public static string Build(
        TransferEngine engine,
        RunConfig config,
        IReadOnlyList<DriveVolume> drives) => BuildResult(engine, config, drives).ToText();

    private static string RiskLabelOf(string current) =>
        DestinationGuard.CheckSourceTree(current).Risk switch
        {
            SourceRisk.None => "正常",
            SourceRisk.Invalid => "路径非法",
            SourceRisk.DriveRoot => "盘根",
            SourceRisk.SystemDirectory => "系统目录",
            _ => "未知",
        };
}
