using DirMover.Core.IO;

namespace DirMover.Core;

/// <summary>
/// 运行时设置：<b>纯内存态</b>，每次启动都回到内置默认值。
/// </summary>
/// <remarks>
/// 为什么坚决不落盘（红线，PRD §5）：一旦"记住上次选择"，用户就可能在一次误操作后
/// 反复以错误的目标根启动，而这类状态没有可见的复位入口。纯内存态让"每次启动都是
/// 已知的安全默认值"成为结构性事实，而不是靠用户记得清空。
///
/// <see cref="Selected"/> 用"按规格表排序的集合"（<see cref="CatalogKeyComparer"/>）：
/// 勾选集合既要能去重，又必须<b>稳定地按 7 项规格顺序</b>枚举——否则确认框里列出的
/// 项目顺序会随哈希顺序漂移，无法与界面逐条核对。
/// </remarks>
public sealed class RunConfig
{
    /// <summary>目标根目录（形如 <c>D:\UserData</c>）；空串表示"没有可用的非系统固定盘"。</summary>
    public string DestinationRoot { get; set; } = string.Empty;

    /// <summary>是否在目标中插入一层 <c>&lt;用户名&gt;</c>。默认勾选。</summary>
    public bool CreateUserDirectory { get; set; } = true;

    /// <summary>勾选集合。只应包含 <see cref="Catalog.AllKeys"/> 中的 key。</summary>
    public ISet<string> Selected { get; set; } = CatalogKeyComparer.CreateSet();

    /// <summary>试运行标记（来自启动参数）。为 <c>true</c> 时完整规划与判定，但不落任何改动。</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// 收敛勾选集合：丢弃未知 key、去重（大小写不敏感）、按 <see cref="Catalog.All"/> 顺序。
    /// </summary>
    /// <remarks>
    /// 必须存在的原因：key 可能来自外部（旧配置/命令行/未来的导入），而"未知 key 被当成
    /// 有效项"会让确认框的项数与实际执行项数不一致——用户以为搬 5 项，实际搬 3 项。
    /// </remarks>
    public void Normalize()
    {
        ISet<string> normalized = CatalogKeyComparer.CreateSet();
        if (Selected is not null)
        {
            foreach (string key in Selected)
            {
                FolderSpec? spec = Catalog.ByKey(key);
                if (spec is not null)
                {
                    // 归一到规格表里的规范大小写，避免 "Documents"/"documents" 变成两项。
                    normalized.Add(spec.Key);
                }
            }
        }

        Selected = normalized;
    }

    /// <summary>
    /// 构造默认配置：全选 7 项、目标根取 <see cref="DriveInventory.PreferredTargetRoot"/>、
    /// 创建当前用户目录为 <c>true</c>。
    /// </summary>
    /// <param name="drives">已枚举到的非系统固定盘。</param>
    /// <param name="dryRun">是否试运行。</param>
    public static RunConfig CreateDefault(IReadOnlyList<DriveVolume> drives, bool dryRun)
    {
        var config = new RunConfig
        {
            DestinationRoot = DriveInventory.PreferredTargetRoot(drives),
            CreateUserDirectory = true,
            DryRun = dryRun,
        };

        foreach (FolderSpec spec in Catalog.All)
        {
            config.Selected.Add(spec.Key);
        }

        config.Normalize();
        return config;
    }

    /// <summary>
    /// 按 PRD §2.1 合成目标路径：<c>&lt;根&gt;\&lt;用户名&gt;\&lt;基名&gt;</c> 或 <c>&lt;根&gt;\&lt;基名&gt;</c>。
    /// </summary>
    /// <param name="spec">文件夹规格（基名取不到时用显示名兜底）。</param>
    /// <param name="currentPath">当前位置（基名取它最后一段，而不是显示名）。</param>
    /// <param name="userName">当前用户名（<see cref="Environment.UserName"/>）。</param>
    /// <returns>
    /// 目标绝对路径；当 <see cref="DestinationRoot"/> 为空（没有任何可用目标盘）时
    /// <b>原样返回当前位置</b>——这样 <c>Same</c> 判定自然为真，该项恒为「无需转移」。
    /// </returns>
    public string ComposeTarget(FolderSpec spec, string currentPath, string userName)
    {
        ArgumentNullException.ThrowIfNull(spec);

        string root = (DestinationRoot ?? string.Empty).Trim();
        if (root.Length == 0)
        {
            return (currentPath ?? string.Empty).Trim();
        }

        string baseName = PathTools.TailName(currentPath);
        if (baseName.Length == 0)
        {
            baseName = spec.Display;
        }

        root = root.TrimEnd('\\', '/');
        if (CreateUserDirectory && !string.IsNullOrWhiteSpace(userName))
        {
            return root + "\\" + userName.Trim() + "\\" + baseName;
        }

        return root + "\\" + baseName;
    }
}

/// <summary>
/// 按 <see cref="Catalog.All"/> 顺序比较 key 的比较器，用于让勾选集合的枚举顺序稳定。
/// </summary>
/// <remarks>
/// 用 <see cref="SortedSet{T}"/> 而不是 <see cref="HashSet{T}"/>：确认框、意图日志、
/// 结果摘要都按"规格表顺序"逐条列出，哈希顺序会让同一次操作的两次展示顺序不同，
/// 无法逐行核对（而逐行核对正是确认框存在的意义）。
/// </remarks>
internal sealed class CatalogKeyComparer : IComparer<string>
{
    /// <summary>无状态单例。</summary>
    public static readonly CatalogKeyComparer Instance = new();

    /// <summary>创建一个使用本比较器的空集合。</summary>
    public static ISet<string> CreateSet() => new SortedSet<string>(Instance);

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        int rankX = Rank(x);
        int rankY = Rank(y);
        if (rankX != rankY)
        {
            return rankX.CompareTo(rankY);
        }

        // 同一序号（含两个未知 key）时按忽略大小写的字典序收敛，保证"同输入同输出"。
        return string.Compare(x ?? string.Empty, y ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return int.MaxValue;
        }

        IReadOnlyList<string> all = Catalog.AllKeys;
        for (int i = 0; i < all.Count; i++)
        {
            if (string.Equals(all[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
