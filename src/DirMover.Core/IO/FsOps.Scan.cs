using System.Security.Cryptography;
using DirMover.Core.Interop;

namespace DirMover.Core.IO;

/// <summary>
/// 一棵目录树的规模统计（确认框与空间闸门的输入）。
/// </summary>
/// <remarks>
/// <see cref="Unreadable"/> 是这批数字里<b>唯一</b>决定"能不能据此判断空间充足"的字段：
/// 只要有 1 项读不到，<see cref="Bytes"/> 就是<b>下界</b>而不是真值，空间闸门必须阻断而不是
/// 拿一个偏小的数字去比。把它做成独立字段（而不是塞进 <see cref="Files"/>）就是为了让
/// "规模可能被低估"这件事在类型层面无法被忽略。
/// </remarks>
public sealed class TreeStat
{
    /// <summary>文件数。</summary>
    public int Files { get; init; }

    /// <summary>子目录数（含空目录，不含根）。</summary>
    public int Dirs { get; init; }

    /// <summary>文件字节数合计。</summary>
    public long Bytes { get; init; }

    /// <summary>链接 / 联接点条目数（不递归，计 0 字节）。</summary>
    public int Links { get; init; }

    /// <summary>枚举期读不到的条目数；&gt; 0 表示规模被低估，引擎必须阻断。</summary>
    public int Unreadable { get; init; }

    /// <summary>规模文案，形如 <c>12 个文件 / 3.4 GB</c>。</summary>
    public string Describe() => $"{Files} 个文件 / {PathTools.HumanSize(Bytes)}";
}

/// <summary>
/// 一个文件的指纹，<b>属于源</b>，不属于目标。
/// </summary>
/// <remarks>
/// <see cref="Digest"/> 在复制时"边读源边算"并挂到 <c>CopyItem</c> 上，校验阶段只用它跟
/// <b>目标</b>比。这条方向性是红线 1 的核心：一旦反过来（校验时重算源摘要），源被改写就
/// 会"以改写后的源为准"判为通过，等于把错的数据当成对的。
/// </remarks>
public sealed class FileFingerprint
{
    /// <summary>字节数。</summary>
    public long Size { get; init; }

    /// <summary>最后写入时间（UTC ticks）。</summary>
    public long ModifiedTicksUtc { get; init; }

    /// <summary>SHA256（十六进制小写）；未计算时为空串。</summary>
    public string Digest { get; init; } = string.Empty;

    /// <summary>卷序列号（与 <see cref="Inode"/> 一起标识"同一物理文件"）。</summary>
    public uint Device { get; init; }

    /// <summary>文件索引（NTFS inode 的等价物）。</summary>
    public ulong Inode { get; init; }

    /// <summary>硬链接数。</summary>
    public uint LinkCount { get; init; }

    /// <summary>文件属性位（稀疏判定用）。</summary>
    public FileAttributes Attributes { get; init; }
}

/// <summary>
/// 源目录清点结果。
/// </summary>
/// <remarks>
/// <see cref="Errors"/> 非空即"整体失败"：清点阶段任何一项读不到都意味着<b>清单不完整</b>，
/// 拿不完整的清单去复制，等于把漏掉的部分留在源目录里，随后删源就会真的丢掉它们。
/// </remarks>
public sealed class SourceTree
{
    /// <summary>子目录相对路径（含空目录，不含根；分隔符为 <c>\</c>）。</summary>
    public List<string> Dirs { get; } = new();

    /// <summary>文件相对路径 → 指纹（不含摘要：摘要在复制时边读边算）。</summary>
    public Dictionary<string, FileFingerprint> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>链接 / 联接点相对路径（<b>不递归</b>）。</summary>
    public List<string> Links { get; } = new();

    /// <summary>按名跳过（<see cref="FsOps.EnumerateSource"/> 的 <c>skipNames</c>）的条目。</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>枚举失败项；非空 → 整体失败（不得静默跳过）。</summary>
    public List<string> Errors { get; } = new();
}

public static partial class FsOps
{
    /// <summary>复制分块大小（1 MiB）：取消与进度都以它为粒度。</summary>
    public const int CopyChunkSize = 1024 * 1024;

    private const int DefaultBufferSize = 64 * 1024;

    /// <summary>
    /// 统计一棵目录树的规模。
    /// </summary>
    /// <param name="root">目录根（可以不存在；此时 <see cref="TreeStat.Unreadable"/> 为 1）。</param>
    /// <param name="limitFiles">
    /// &gt; 0 时只数到这么多文件就停（确认框只需要量级）。此时结果同样是<b>下界</b>，
    /// 但不需要阻断——它只用于展示，不用于空间判定。
    /// </param>
    public static TreeStat ScanTree(string root, int limitFiles = 0)
    {
        var sink = new WalkSink { LimitFiles = Math.Max(0, limitFiles) };
        if (string.IsNullOrWhiteSpace(root))
        {
            sink.Unreadable++;
            return new TreeStat { Unreadable = sink.Unreadable };
        }

        Walk(root, skipNames: null, sink);
        return new TreeStat
        {
            Files = sink.Files,
            Dirs = sink.Dirs,
            Bytes = sink.Bytes,
            Links = sink.Links,
            Unreadable = sink.Unreadable,
        };
    }

    /// <summary>
    /// 清点源目录：文件指纹、空目录、链接、按名跳过项、不可读项。
    /// </summary>
    /// <param name="root">源根目录。</param>
    /// <param name="skipNames">按<b>条目名</b>跳过的名字（例如目标根目录恰好嵌在源里时要避开）。</param>
    /// <remarks>
    /// 三条硬性口径：
    /// ① 空目录必须进 <see cref="SourceTree.Dirs"/>（否则目标侧会缺目录，校验维度 ⑥ 也过不了）；
    /// ② 重解析点只登记、<b>不递归</b>（递归会顺着 <c>Desktop</c> 之类的联接点把整个用户目录算进来，
    ///    既让规模统计失真，也可能把源之外的区域复制进去）；
    /// ③ 任何读失败都进 <see cref="SourceTree.Errors"/>，<b>绝不静默跳过</b>——静默跳过 + 删源 = 丢数据。
    /// </remarks>
    public static SourceTree EnumerateSource(string root, IReadOnlyCollection<string>? skipNames = null)
    {
        var tree = new SourceTree();
        if (string.IsNullOrWhiteSpace(root))
        {
            tree.Errors.Add("源目录为空");
            return tree;
        }

        if (!PathTools.Exists(root))
        {
            tree.Errors.Add(UiText.CurrentMissing(root));
            return tree;
        }

        var sink = new WalkSink { Tree = tree };
        Walk(root, skipNames, sink);
        return tree;
    }

    /// <summary>
    /// 读一个文件的指纹。读不到属性时保留大小/时间（<see cref="FileFingerprint.Device"/> 等为 0），
    /// 不抛——调用方对"读不到"的处理策略各不相同（清点要报错、校验要记维度失败）。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="withDigest">是否顺带算 SHA256（清点阶段不算：复制时会边读边算）。</param>
    internal static FileFingerprint FingerprintOf(string path, bool withDigest)
    {
        var file = new FileInfo(PathTools.ToExtended(path));
        if (!file.Exists)
        {
            throw new FileNotFoundException($"文件不存在：{path}", path);
        }

        long size = file.Length;
        long modified = file.LastWriteTimeUtc.Ticks;
        FileAttributes attributes = file.Attributes;

        uint device = 0;
        ulong inode = 0;
        uint linkCount = 1;

        IntPtr handle = NativeMethods.CreateFileW(
            file.FullName,
            NativeMethods.GenericRead,
            NativeMethods.FileShareAll,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);

        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            try
            {
                if (NativeMethods.GetFileInformationByHandle(handle, out NativeMethods.ByHandleFileInformation info))
                {
                    device = info.VolumeSerialNumber;
                    inode = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                    linkCount = info.NumberOfLinks == 0 ? 1 : info.NumberOfLinks;
                }
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        return new FileFingerprint
        {
            Size = size,
            ModifiedTicksUtc = modified,
            Digest = withDigest ? ComputeDigest(path) : string.Empty,
            Device = device,
            Inode = inode,
            LinkCount = linkCount,
            Attributes = attributes,
        };
    }

    /// <summary>
    /// 算文件的 SHA256（十六进制小写）。流式读取，不把整个文件读进内存。
    /// </summary>
    /// <param name="path">文件路径。</param>
    internal static string ComputeDigest(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(
            PathTools.ToExtended(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            DefaultBufferSize,
            FileOptions.SequentialScan);

        // 池化缓冲区：校验阶段按文件逐个调用本方法，每调用一次 new 一个 1 MiB 数组
        // 会全部落到大对象堆；池化后同一次校验全程复用。
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyChunkSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, CopyChunkSize)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return Compat.HexUpper(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>条目是否重解析点（属性位或 <c>LinkTarget</c> 任一命中即算）。</summary>
    /// <param name="info">条目。</param>
    internal static bool IsLink(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        try
        {
            return Compat.GetLinkTarget(info) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // ================================================================ 内部

    private static void Walk(string root, IReadOnlyCollection<string>? skipNames, WalkSink sink)
    {
        HashSet<string>? skip = null;
        if (skipNames is { Count: > 0 })
        {
            skip = new HashSet<string>(skipNames.Where(static n => !string.IsNullOrWhiteSpace(n)),
                StringComparer.OrdinalIgnoreCase);
        }

        var stack = new Stack<(string Absolute, string Relative)>();
        stack.Push((PathTools.StripExtendedPrefix(root), string.Empty));

        while (stack.Count > 0)
        {
            (string absolute, string relative) = stack.Pop();

            FileSystemInfo[] entries;
            try
            {
                entries = new DirectoryInfo(PathTools.ToExtended(absolute)).GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
            {
                sink.Unreadable++;
                sink.Tree?.Errors.Add($"{Describe(relative)}: {ex.Message}");
                continue;
            }

            foreach (FileSystemInfo info in entries)
            {
                string childRel = relative.Length == 0 ? info.Name : relative + "\\" + info.Name;

                if (skip is not null && skip.Contains(info.Name))
                {
                    sink.Tree?.Skipped.Add(childRel);
                    continue;
                }

                try
                {
                    if (IsLink(info))
                    {
                        sink.Links++;
                        sink.Tree?.Links.Add(childRel);
                        continue;
                    }

                    if (info is DirectoryInfo directory)
                    {
                        sink.Dirs++;
                        sink.Tree?.Dirs.Add(childRel);
                        stack.Push((directory.FullName, childRel));
                        continue;
                    }

                    long size = 0;
                    try
                    {
                        size = ((FileInfo)info).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 文件在枚举与取长度之间消失：这一项必须可见，否则清单会少一个文件。
                        sink.Unreadable++;
                        sink.Tree?.Errors.Add($"{childRel}: {ex.Message}");
                        continue;
                    }

                    sink.Files++;
                    sink.Bytes += size;
                    if (sink.Tree is not null)
                    {
                        sink.Tree.Files[childRel] = FingerprintOf(info.FullName, withDigest: false);
                    }

                    if (sink.LimitFiles > 0 && sink.Files >= sink.LimitFiles)
                    {
                        sink.Limited = true;
                        return;
                    }
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
                {
                    sink.Unreadable++;
                    sink.Tree?.Errors.Add($"{childRel}: {ex.Message}");
                }
            }
        }
    }

    private static string Describe(string relative) => relative.Length == 0 ? "(根目录)" : relative;

    private sealed class WalkSink
    {
        public SourceTree? Tree { get; init; }

        public int LimitFiles { get; init; }

        public int Files { get; set; }

        public long Bytes { get; set; }

        public int Dirs { get; set; }

        public int Links { get; set; }

        public int Unreadable { get; set; }

        public bool Limited { get; set; }
    }
}
