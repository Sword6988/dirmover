using System.Globalization;
using System.Text;
using DirMover.Core.Interop;

namespace DirMover.Core.IO;

/// <summary>
/// 一个可用的目标卷（非系统固定盘）。
/// </summary>
/// <param name="Letter">盘符，形如 <c>D:</c>。</param>
/// <param name="Root">卷根，形如 <c>D:\</c>。</param>
/// <param name="TotalBytes">总容量。</param>
/// <param name="FreeBytes">可用容量。</param>
public sealed record DriveVolume(string Letter, string Root, long TotalBytes, long FreeBytes)
{
    /// <summary>该卷上固定的目标根目录：<c>&lt;盘符&gt;:\UserData</c>。</summary>
    public string TargetRoot => Root.TrimEnd('\\') + "\\" + Catalog.DefaultTargetLeaf;

    /// <summary>空间回显文案，形如 <c>D:\  可用 1.2 TB / 2.0 TB</c>。</summary>
    public string SpaceLabel =>
        $"{Root}  可用 {PathTools.HumanSize(FreeBytes)} / {PathTools.HumanSize(TotalBytes)}";
}

/// <summary>
/// 目标盘枚举。
/// </summary>
/// <remarks>
/// 判定口径固定为 <c>GetDriveTypeW == DRIVE_FIXED</c> 且盘符 ≠ 系统盘。
/// 之所以不用 <c>DriveInfo.DriveType</c>：两者在 ReFS / 动态盘 / 存储池 / VHD 挂载盘上
/// 不一定等价，换实现会出现"某类盘在原版可选、重写后从下拉里消失"。下拉里少了盘 =
/// 用户无法选到目标 = 功能缺失，因此这里与原实现逐字对齐。
/// </remarks>
public static class DriveInventory
{
    /// <summary>枚举全部"非系统固定盘"，按盘符升序。</summary>
    public static IReadOnlyList<DriveVolume> ListFixedNonSystem()
    {
        var drives = new List<DriveVolume>(8);
        string systemDrive = Catalog.SystemDrive;

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string name = letter + ":";
            if (string.Equals(name, systemDrive, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string root = name + "\\";
            if (NativeMethods.GetDriveTypeW(root) != NativeMethods.DriveFixed)
            {
                continue;
            }

            // 读不到容量的盘不进入下拉：选项最终要参与空间闸门，读不到容量就无法判断能否装下。
            if (!NativeMethods.GetDiskFreeSpaceExW(root, out ulong freeBytes, out ulong totalBytes, out _))
            {
                continue;
            }

            drives.Add(new DriveVolume(
                name,
                root,
                (long)Math.Min(totalBytes, long.MaxValue),
                (long)Math.Min(freeBytes, long.MaxValue)));
        }

        return drives;
    }

    /// <summary>
    /// 默认目标根：<c>D:\UserData</c> 可用则取之，否则取首个可用盘；一个都没有 → 空串。
    /// </summary>
    /// <param name="drives">已枚举到的盘。</param>
    public static string PreferredTargetRoot(IReadOnlyList<DriveVolume> drives)
    {
        if (drives is null || drives.Count == 0)
        {
            return string.Empty;
        }

        foreach (DriveVolume drive in drives)
        {
            if (string.Equals(drive.Letter, "D:", StringComparison.OrdinalIgnoreCase))
            {
                return drive.TargetRoot;
            }
        }

        return drives[0].TargetRoot;
    }
}

/// <summary>
/// 纯托管路径工具：长路径前缀、规范化、同路径/包含判定、物理同位置、8.3 长名还原、
/// 文件系统可用空间、体积格式化。
/// </summary>
/// <remarks>
/// 关键约定（长路径）：<c>\\?\</c> 前缀<b>只在真正调用 Win32/BCL 的那一刻</b>才加；
/// 一切比较、相对路径计算、界面展示都使用"干净路径"。原因有二：
/// ① <c>\\?\</c> 路径不做 <c>.</c>/<c>..</c> 归一化，带前缀比较会得到既不等也不包含的结果；
/// ② <c>GetLongPathNameW</c>/<c>DeviceIoControl</c>/<c>File.OpenHandle</c> 对前缀的容忍度不同，
///    混用会退化成 <c>DirectoryNotFoundException</c>，进而被误判为"源不存在"。
/// </remarks>
public static class PathTools
{
    private const string ExtendedPrefix = @"\\?\";
    private const string UncExtendedPrefix = @"\\?\UNC\";
    private const string DevicePrefix = @"\\.\";

    /// <summary>
    /// 需要时加 <c>\\?\</c> 扩展前缀（达到 <see cref="Catalog.LongPathThreshold"/> 才加）。
    /// </summary>
    /// <param name="path">干净路径。</param>
    public static string ToExtended(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // 重复加前缀会让 API 直接失败，故已带前缀的原样返回。
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ||
            path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        if (path.Length < Catalog.LongPathThreshold)
        {
            return path;
        }

        string absolute = SafeGetFullPath(path);
        if (absolute.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return UncExtendedPrefix + absolute.Substring(2);
        }

        return ExtendedPrefix + absolute;
    }

    /// <summary>剥掉 <c>\\?\</c> / <c>\\?\UNC\</c> 前缀（等价于原实现的 <c>_unlp</c>）。</summary>
    /// <param name="path">可能带扩展前缀的路径。</param>
    public static string FromExtended(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (path.StartsWith(UncExtendedPrefix, StringComparison.Ordinal))
        {
            return @"\\" + path.Substring(UncExtendedPrefix.Length);
        }

        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return path.Substring(ExtendedPrefix.Length);
        }

        return path;
    }

    /// <summary>
    /// 剥掉全部 Win32 扩展前缀（含 <c>\\.\</c>）。
    /// </summary>
    /// <remarks>
    /// 所有参与"禁区比较"的路径都必须先过这一道：<c>\\?\C:\Windows</c> 与 <c>C:\Windows</c>
    /// 是同一目录，纯字符串比较会让"不许搬系统目录"的闸门被前缀绕过。
    /// </remarks>
    /// <param name="path">可能带任意扩展前缀的路径。</param>
    public static string StripExtendedPrefix(string path)
    {
        string cleaned = FromExtended(path);
        if (cleaned.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            cleaned = cleaned.Substring(DevicePrefix.Length);
        }

        return cleaned;
    }

    /// <summary>展开 <c>%VAR%</c> 形式的环境变量。</summary>
    /// <param name="path">可能含环境变量的路径。</param>
    public static string Expand(string path) =>
        string.IsNullOrEmpty(path) ? string.Empty : Environment.ExpandEnvironmentVariables(path);

    /// <summary>
    /// 规范化路径用于<b>比较</b>：剥前缀 + 绝对化 + 去尾分隔符（盘根保留）+ 小写。
    /// </summary>
    /// <param name="path">路径。</param>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string absolute = SafeGetFullPath(StripExtendedPrefix(path));
        string root = Path.GetPathRoot(absolute) ?? string.Empty;
        if (absolute.Length > root.Length)
        {
            absolute = absolute.TrimEnd('\\', '/');
        }

        return absolute.ToLowerInvariant();
    }

    /// <summary>
    /// 两个路径是否同一位置（大小写与尾分隔符不敏感）。
    /// </summary>
    /// <param name="a">路径 A。</param>
    /// <param name="b">路径 B。</param>
    /// <remarks>
    /// 任一为空即视为"不同"：两个空串被判为相同会让"当前位置未知"的项目被当成
    /// 「路径相同，无需转移」而静默跳过（那是漏项，不是跳过）。
    /// </remarks>
    public static bool SamePath(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);
    }

    /// <summary><paramref name="child"/> 是否位于 <paramref name="parent"/> 之内（含自身）。</summary>
    /// <param name="child">子路径。</param>
    /// <param name="parent">父路径。</param>
    public static bool IsWithin(string child, string parent)
    {
        string c = Normalize(child);
        string p = Normalize(parent);
        if (c.Length == 0 || p.Length == 0)
        {
            return false;
        }

        if (string.Equals(c, p, StringComparison.Ordinal))
        {
            return true;
        }

        return c.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// 两个路径是否指向<b>同一物理位置</b>（穿透 junction / 符号链接 / subst 盘符别名）。
    /// </summary>
    /// <param name="a">路径 A。</param>
    /// <param name="b">路径 B。</param>
    /// <remarks>
    /// 为什么必须穿透：<see cref="SamePath"/> 只做字符串比较，把目标写成"指向源的 junction"
    /// 时字符串不同而物理位置相同，随后「复制（全部命中复用）→ 校验通过 → 写注册表 → 删源」
    /// 会把用户真实目录删掉、注册表指向悬空路径。
    /// </remarks>
    public static bool ResolvesToSame(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            string? resolvedA = RealPath(a);
            string? resolvedB = RealPath(b);
            if (!string.IsNullOrEmpty(resolvedA) && !string.IsNullOrEmpty(resolvedB))
            {
                return string.Equals(Normalize(resolvedA), Normalize(resolvedB), StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 解析失败退回字符串比较（fail-safe：既不因为解析失败而放行，也不抛异常）。
        }

        return SamePath(a, b);
    }

    /// <summary>
    /// 把路径中<b>已存在</b>的部分还原为长名，解除 8.3 短名别名。
    /// </summary>
    /// <param name="path">可能含短名的路径。</param>
    /// <remarks>
    /// <c>C:\PROGRA~1</c> 与 <c>C:\Program Files</c> 是同一目录、字符串不同。不还原的话
    /// "不许搬到系统目录"会被短名绕过。任何一步失败都<b>原样返回</b>——宁可漏还原，
    /// 也不能返回一个比输入更错误的路径。
    /// </remarks>
    public static string LongPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string probe = SafeGetFullPath(StripExtendedPrefix(path));
        var missing = new List<string>();
        while (!PathExists(probe))
        {
            string? parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal))
            {
                return path;
            }

            missing.Add(Path.GetFileName(probe));
            probe = parent;
        }

        var buffer = new StringBuilder(32768);
        uint length = NativeMethods.GetLongPathNameW(probe, buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
        {
            return path;
        }

        string restored = buffer.ToString();
        for (int i = missing.Count - 1; i >= 0; i--)
        {
            restored = Path.Combine(restored, missing[i]);
        }

        return restored;
    }

    /// <summary>路径最后一段的名字（目录基名，不是显示名）。</summary>
    /// <param name="path">路径。</param>
    public static string TailName(string path)
    {
        string cleaned = (path ?? string.Empty).TrimEnd('\\', '/');
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        return Path.GetFileName(cleaned);
    }

    /// <summary>
    /// 人类可读体积：1024 进制、1 位小数；<c>B</c> 不带小数。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    public static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes < 0 ? 0 : bytes;

        for (int i = 0; i < units.Length; i++)
        {
            if (value < 1024 || i == units.Length - 1)
            {
                return i == 0
                    ? string.Format(CultureInfo.InvariantCulture, "{0} B", (long)value)
                    : string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", value, units[i]);
            }

            value /= 1024;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} TB", value);
    }

    /// <summary>
    /// 卷标识（优先卷 GUID，取不到回退盘符小写）。用于判断"是否同一卷"（同卷可原子改名）。
    /// </summary>
    /// <param name="path">路径（可以不存在）。</param>
    public static string VolumeKey(string path)
    {
        string probe = DeepestExistingAncestor(path);
        if (probe.Length == 0)
        {
            return string.Empty;
        }

        string root = Path.GetPathRoot(probe) ?? string.Empty;
        if (root.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(1024);
        if (NativeMethods.GetVolumeNameForVolumeMountPointW(root, buffer, (uint)buffer.Capacity))
        {
            string guid = buffer.ToString().TrimEnd('\\');
            if (guid.Length > 0)
            {
                return guid.ToLowerInvariant();
            }
        }

        return root.TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// 目标所在卷的可用字节数；<b>取不到返回 <c>null</c> 表示"未知"</b>。
    /// </summary>
    /// <param name="path">路径（可以不存在，会向上找最深的已存在祖先）。</param>
    /// <remarks>
    /// 绝不能用 0 表达"未知"：调用方一旦把 0 当成"无需检查"，探测失败时就会一路复制到
    /// 磁盘写满（原项目的历史缺陷）。<c>null</c> 强制调用方显式阻断。
    /// </remarks>
    public static long? FreeSpace(string path)
    {
        string probe = DeepestExistingAncestor(path);
        if (probe.Length == 0)
        {
            return null;
        }

        if (!NativeMethods.GetDiskFreeSpaceExW(probe, out ulong freeBytes, out _, out _))
        {
            return null;
        }

        return (long)Math.Min(freeBytes, long.MaxValue);
    }

    /// <summary>
    /// 指定路径自身是否为重解析点（junction / 符号链接 / 挂载点）。
    /// 不存在的路径返回 <c>false</c>——工具自己新建目标目录是正常场景，不能误伤。
    /// </summary>
    /// <param name="path">路径。</param>
    public static bool IsReparsePoint(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string clean = StripExtendedPrefix(path);
        // 存在性/属性检查必须带 \\?\ 前缀：未开启 LongPathsEnabled 的机器上，>260 字符的
        // 裸路径会让 Directory.Exists/File.Exists 返回 false，重解析点判定被静默降级成
        // "不是链接"（fail-open），联接点伪装的正常目录就绕过了祖先链闸门。
        string extended = ToExtended(clean);
        try
        {
            bool isDirectory = Directory.Exists(extended);
            bool isFile = !isDirectory && File.Exists(extended);
            if (!isDirectory && !isFile)
            {
                return false;
            }

            if ((File.GetAttributes(extended) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            // 双保险：属性位在某些文件系统/联接点形态下不稳定，LinkTarget 非空即链接。
            FileSystemInfo info = isDirectory
                ? new DirectoryInfo(extended)
                : new FileInfo(extended);
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 读不到属性时按"不是链接"处理：真正的链接会在后续建目录/复制时暴露，
            // 而这里抛异常会让合法的迁移路径整体失败。
            return false;
        }
    }

    /// <summary>
    /// 路径是否存在（文件或目录）。内部按需加 <c>\\?\</c> 前缀。
    /// </summary>
    /// <param name="path">路径。</param>
    /// <remarks>
    /// 为什么要单独提供这个方法，而不是让调用方直接用 <c>Directory.Exists</c>：
    /// 未加扩展前缀时，超过 MAX_PATH 的合法路径会让 <c>Directory.Exists</c> 返回
    /// <b>false</b>——于是"源目录明明在，却被判为不存在"。这在迁移场景里是直接的
    /// 数据事故（原项目修过同类缺陷）。所有"存在性"判断都必须走这里。
    /// </remarks>
    public static bool Exists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string extended = ToExtended(StripExtendedPrefix(path));
        try
        {
            return Directory.Exists(extended) || File.Exists(extended);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 逐级解析链接后的真实路径（供禁区判定使用：注册表可能被改成"指向禁区的链接"）。
    /// </summary>
    /// <param name="path">路径。</param>
    internal static string ResolveLinks(string path) => RealPath(path);

    // ================================================================ 内部

    private static string SafeGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path ?? string.Empty;
        }
    }

    private static bool PathExists(string path) => Exists(path);

    private static string DeepestExistingAncestor(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string probe = SafeGetFullPath(StripExtendedPrefix(path));
        while (probe.Length > 0 && !PathExists(probe))
        {
            string? parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal))
            {
                return probe;
            }

            probe = parent;
        }

        return probe;
    }

    /// <summary>
    /// 逐级解析链接后的真实路径；遇到不存在的层级即停止并返回当前结果。
    /// </summary>
    private static string RealPath(string path)
    {
        string full = SafeGetFullPath(StripExtendedPrefix(path));
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string rest = full.Substring(Math.Min(root.Length, full.Length));
        string current = root;

        foreach (string part in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                // 带 \\?\ 前缀做存在性检查：未开启 LongPathsEnabled 的机器上裸路径 >260 字符
                // 会被判为"不存在"，链接展开被静默跳过（ResolvesToSame 退化成字符串比较）。
                string extended = ToExtended(current);
                bool isDirectory = Directory.Exists(extended);
                bool isFile = !isDirectory && File.Exists(extended);
                if (!isDirectory && !isFile)
                {
                    return full;
                }

                FileSystemInfo info = isDirectory
                    ? new DirectoryInfo(extended)
                    : new FileInfo(extended);
                FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    // 回写到"干净路径"：内部约定一切比较与拼接都用不带前缀的形态。
                    current = StripExtendedPrefix(target.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return full;
            }
        }

        return current;
    }
}
