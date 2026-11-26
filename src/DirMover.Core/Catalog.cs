namespace DirMover.Core;

/// <summary>
/// 一个可转移的用户文件夹的静态规格。
/// </summary>
/// <param name="Key">稳定标识（"documents" …），用于勾选集合与日志，不面向用户。</param>
/// <param name="Display">界面显示名（"我的文档" …）。<b>只上屏</b>，绝不参与路径合成。</param>
/// <param name="ValueName">权威注册表值名。注意 <c>downloads</c> 的权威值名本身就是一个 GUID。</param>
/// <param name="DefaultPath">系统出厂默认路径，<b>保留</b> <c>%USERPROFILE%</c> 形态（写回注册表时要与系统原生写法一致）。</param>
/// <param name="Aliases">需要一并同步的其它值名（GUID）。写入时只更新"注册表中已存在"的别名，绝不凭空创建。</param>
public sealed record FolderSpec(
    string Key,
    string Display,
    string ValueName,
    string DefaultPath,
    IReadOnlyList<string> Aliases);

/// <summary>
/// 静态真源：注册表位置、7 项文件夹规格、空间余量、各类等待时长、禁区名单。
/// </summary>
/// <remarks>
/// 为什么这些常量必须集中在一处（而不是就近定义）：
/// 同一批数字会被<b>至少两处</b>复述——引擎的空间闸门与系统集成的超时，以及<b>界面文案</b>
/// （"最长约 20 秒"这类说法直接来自 <see cref="ExplorerKillTimeout"/>）。散落成字面量后，
/// 改一处就会让界面继续复述旧数字，用户看到的行为说明与实际行为静默脱节。
/// 同理，禁区名单必须由"当前系统盘"现算：原实现曾在目标侧动态化、源侧仍写死 <c>C:\</c>，
/// 于是在系统盘不是 C: 的机器上源侧整道闸门 fail-open。
/// </remarks>
public static class Catalog
{
    // ------------------------------------------------------------------ 注册表位置

    /// <summary>Explorer 子键（相对 HKCU）。</summary>
    public const string ExplorerSubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    /// <summary>权威子键：迁移结果的唯一权威写入位置。</summary>
    public const string UserShellFoldersSubKey = ExplorerSubKey + @"\User Shell Folders";

    /// <summary>缓存同步子键：只写其中<b>已存在</b>的同名值，绝不新建（作用是免重启资源管理器生效）。</summary>
    public const string ShellFoldersSubKey = ExplorerSubKey + @"\Shell Folders";

    /// <summary>系统外壳重启开关所在位置（HKLM，只读）。</summary>
    public const string WinlogonSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    // ------------------------------------------------------------------ 应用元信息

    /// <summary>与上游版本对齐的应用版本号。</summary>
    public const string AppVersion = "3.0.1";

    /// <summary>目标根目录固定叶子名：取值恒为 <c>&lt;盘&gt;:\UserData</c>。</summary>
    public const string DefaultTargetLeaf = "UserData";

    /// <summary>单实例命名互斥体名。<c>Local\</c> 表示按登录会话隔离（多用户/RDP 各可跑一份）。</summary>
    public const string SingleInstanceName = @"Local\DirMover_Single";

    // ------------------------------------------------------------------ 空间与等待

    /// <summary>空间闸门的比例余量：<c>needed × 1.05</c>。</summary>
    public const double SpaceMarginRatio = 1.05;

    /// <summary>空间闸门的固定余量（64 MiB）。</summary>
    public const long SpaceMarginBytes = 64L * 1024 * 1024;

    /// <summary>结束 explorer 进程的等待超时。</summary>
    public static readonly TimeSpan ExplorerKillTimeout = TimeSpan.FromSeconds(20);

    /// <summary>结束 explorer 之后的静置等待（等它真的退出、句柄释放）。</summary>
    public static readonly TimeSpan ExplorerTermWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// AutoRestartShell 的自动拉起宽限：确认旧 explorer 已退出后，先轮询等系统把
    /// shell 拉回来；宽限内没回来才由本程序显式拉起。
    /// </summary>
    /// <remarks>
    /// winlogon 重启 shell 不是瞬时的（实机观察 1–3 s，负载高更久）。旧实现固定 1 s
    /// 只查一次：宽限太短会导致"系统本来要拉、我们先抢了"或"系统还没来得及拉、我们
    /// 判定它不会拉"，两种都制造多余的 explorer 或多余的失败。
    /// </remarks>
    public static readonly TimeSpan ExplorerAutoRespawnWait = TimeSpan.FromSeconds(3);

    /// <summary>显式拉起 explorer 之后的存活确认预算（轮询，出现即算存活）。</summary>
    /// <remarks>
    /// 旧实现固定 1 s 只查一次：拉起通道本身可能要重试（shell 基础设施刚随 explorer
    /// 一起死掉），进程创建也可能慢于 1 s。预算内反复查，出现即返回。
    /// </remarks>
    public static readonly TimeSpan ExplorerRelaunchTotalWait = TimeSpan.FromSeconds(10);

    /// <summary>explorer 存活 / 自动拉起轮询的间隔。</summary>
    public static readonly TimeSpan ExplorerRelaunchPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>删源退避重试的第一次等待。</summary>
    public static readonly TimeSpan RemoveBackoff1 = TimeSpan.FromMilliseconds(200);

    /// <summary>删源退避重试的第二次等待。</summary>
    public static readonly TimeSpan RemoveBackoff2 = TimeSpan.FromMilliseconds(500);

    /// <summary>整树删除的总尝试次数（含首次）。</summary>
    public const int RemoveAttempts = 3;

    /// <summary>进度上报节流窗口：≥100 ms 或"最后一个文件"才放行。</summary>
    public static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(100);

    /// <summary>关窗收尾的轮询间隔。</summary>
    public static readonly TimeSpan ExitFinishPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>关窗收尾的等待上限；超时记日志后强制退出。</summary>
    public static readonly TimeSpan ExitFinishTimeout = TimeSpan.FromSeconds(60);

    /// <summary>启动后等待多久再检查遗留事务（让窗口先画出来）。</summary>
    public static readonly TimeSpan PendingIntentDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>达到该字符数即加 <c>\\?\</c> 扩展前缀（留出余量，不卡 260）。</summary>
    public const int LongPathThreshold = 240;

    // ------------------------------------------------------------------ 已知文件夹 GUID

    internal const string GuidDocuments = "{F42EE2D3-909F-4907-8871-4C22FC0BF756}";
    internal const string GuidFavorites = "{1777F761-68AD-4D8A-87BD-30B759FA33DD}";
    internal const string GuidPictures = "{0DDD015D-B06C-45D5-8C4C-F59713854639}";
    internal const string GuidMusic = "{A0C69A99-21C8-4671-8703-7934162FCF1D}";
    internal const string GuidVideos = "{35286A68-3C57-41A1-BBB1-0EAE73D76C95}";

    /// <summary><c>downloads</c> 的<b>权威</b>值名（本身即 GUID，不存在传统值名）。</summary>
    internal const string GuidDownloads = "{374DE290-123F-4565-9164-39C4925E467B}";

    /// <summary><c>downloads</c> 的别名（与权威值名是两个不同的 GUID，混淆会导致"资源管理器显示 A、系统实际认 B"）。</summary>
    internal const string GuidDownloadsAlt = "{7D83EE9B-2244-4E70-B1F5-5393042AF1E4}";

    internal const string GuidDesktop = "{754AC886-DF64-4CBA-86B5-F7FBF4FBCEF5}";

    // ------------------------------------------------------------------ 7 项规格（顺序即界面顺序）

    /// <remarks>
    /// 规格表里<b>刻意不放</b>"目标目录名"这一列：那个名字必须按<b>运行机器</b>的 UI 语言在
    /// 运行时解析（见 <see cref="IO.FolderDisplayName"/>），任何写死的语言都会把别的机器改错。
    /// <c>DefaultPath</c> 的英文物理名只在"恢复默认值"时写回注册表，不作为目标叶子名。
    /// </remarks>
    private static readonly IReadOnlyList<FolderSpec> FolderList = new FolderSpec[]
    {
        new("documents", "我的文档", "Personal", @"%USERPROFILE%\Documents",
            new[] { GuidDocuments }),
        new("favorites", "收藏夹", "Favorites", @"%USERPROFILE%\Favorites",
            new[] { GuidFavorites }),
        new("pictures", "我的图片", "My Pictures", @"%USERPROFILE%\Pictures",
            new[] { GuidPictures }),
        new("music", "我的音乐", "My Music", @"%USERPROFILE%\Music",
            new[] { GuidMusic }),
        new("videos", "我的视频", "My Video", @"%USERPROFILE%\Videos",
            new[] { GuidVideos }),

        // 唯一一项"权威值名是 GUID"的规格：value_name 与 alias 都是 GUID，且互不相同。
        new("downloads", "下载目录", GuidDownloads, @"%USERPROFILE%\Downloads",
            new[] { GuidDownloadsAlt }),

        new("desktop", "桌面", "Desktop", @"%USERPROFILE%\Desktop",
            new[] { GuidDesktop }),
    };

    private static readonly IReadOnlyList<string> KeyList =
        FolderList.Select(static spec => spec.Key).ToArray();

    /// <summary>7 项规格，顺序即界面顺序。</summary>
    public static IReadOnlyList<FolderSpec> All => FolderList;

    /// <summary>7 个稳定 key，顺序与 <see cref="All"/> 一致。</summary>
    public static IReadOnlyList<string> AllKeys => KeyList;

    /// <summary>
    /// 按 key 取规格；未知 key 返回 <c>null</c>（调用方必须忽略，不能抛异常——
    /// key 可能来自上一次运行留下的旧数据）。
    /// </summary>
    /// <param name="key">稳定标识。</param>
    public static FolderSpec? ByKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        foreach (FolderSpec spec in FolderList)
        {
            if (string.Equals(spec.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return spec;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ 系统盘与禁区

    private static string _systemDrive = ResolveSystemDrive();

    /// <summary>
    /// 当前系统盘（形如 <c>C:</c>，大写、无尾分隔符）。
    /// </summary>
    /// <remarks>
    /// setter 为 <c>internal</c>：单测需要模拟"系统盘不是 C:"的机器，否则"禁区名单
    /// 随系统盘现算"这条性质无法被验证（写死 C: 是原项目的历史缺陷）。
    /// </remarks>
    public static string SystemDrive
    {
        get => _systemDrive;
        internal set => _systemDrive = NormalizeDrive(value);
    }

    /// <summary>系统盘下需要<b>整棵子树</b>拉黑的敏感目录（相对系统盘根的名字）。</summary>
    private static readonly string[] SensitiveTrees =
    {
        "Windows",
        "Program Files",
        "Program Files (x86)",
        "ProgramData",
        "PerfLogs",
        "Recovery",
    };

    /// <summary>
    /// 只做<b>精确匹配</b>的敏感目录。
    /// </summary>
    /// <remarks>
    /// <c>\Users</c> 不能做子树匹配：用户把资料目录放在 <c>C:\Users\me\MyDocs</c> 是
    /// 完全合法的状态。只拦"源本身就是 <c>\Users</c>"这一种（否则会把合法资料判定为禁区）。
    /// </remarks>
    private const string SensitiveExactOnly = "Users";

    /// <summary>
    /// 目标禁区：系统盘下全部敏感目录（含仅精确匹配的那一项）。
    /// 调用方对整份名单统一使用"包含"语义（目标落在其中任一目录之下即拒绝）。
    /// </summary>
    public static IReadOnlyList<string> ForbiddenTargetRoots() =>
        BuildAbsoluteList(SensitiveTrees, includeExactOnly: true);

    /// <summary>源禁区中需要"整棵子树"拒绝的部分。</summary>
    public static IReadOnlyList<string> ForbiddenSourceTrees() =>
        BuildAbsoluteList(SensitiveTrees, includeExactOnly: false);

    /// <summary>源禁区中需要"精确匹配"的部分（子树 + <c>\Users</c>）。</summary>
    public static IReadOnlyList<string> ForbiddenSourceExact() =>
        BuildAbsoluteList(SensitiveTrees, includeExactOnly: true);

    private static IReadOnlyList<string> BuildAbsoluteList(string[] names, bool includeExactOnly)
    {
        // 每次现算（而不是模块加载时算一次）：SystemDrive 可被测试改写，
        // 缓存会让"随系统盘变化"这条性质失效。
        string drive = _systemDrive;
        var result = new List<string>(names.Length + 1);
        foreach (string name in names)
        {
            result.Add(drive + "\\" + name);
        }

        if (includeExactOnly)
        {
            result.Add(drive + "\\" + SensitiveExactOnly);
        }

        return result;
    }

    private static string ResolveSystemDrive()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("SystemDrive");
        return NormalizeDrive(fromEnvironment ?? string.Empty);
    }

    private static string NormalizeDrive(string? value)
    {
        string drive = (value ?? string.Empty).Trim().TrimEnd('\\', '/').ToUpperInvariant();
        return drive.Length >= 2 ? drive : "C:";
    }
}
