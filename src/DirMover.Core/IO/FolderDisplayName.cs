using System.Text;
using DirMover.Core.Interop;

namespace DirMover.Core.IO;

/// <summary>
/// 解析"已知文件夹在<b>当前运行机器</b>上的本地化显示名"，用作转移目标的目录叶子名。
/// </summary>
/// <remarks>
/// <b>为什么这里一个具体语言的目录名都不能有</b>：报障机器与构建机可能是不同语言
/// （简体/繁体中文、日文、英文……），用户也可能自定义过文件夹显示名。Core 里写死任何一种
/// 语言，都会把别人的目录强行改名，反而制造新的"名称不对"。
/// <para>
/// 名字的可移植真源是源目录里的 <c>desktop.ini</c>：Windows 把本地化显示名写在
/// <c>[.ShellClassInfo] LocalizedResourceName=</c>，常见形态是间接串
/// <c>@%SystemRoot%\system32\shell32.dll,-21770</c>。交给
/// <c>shlwapi!SHLoadIndirectString</c> 解析，它按运行机器的 MUI / UI 语言返回对应文本，
/// 由工具决定语言这件事就不存在了。字面量形态（用户自定义名）直接原样使用。
/// </para>
/// <para>
/// <b>为什么不走 SHGetLocalizedName / SHGetKnownFolderItem</b>：前者要求目录仍带
/// System/ReadOnly 属性，而本工具的复制路径恰好会丢掉这些属性（这正是"名称变英文"的成因
/// 之一），实测在已迁移过的目录上返回 <c>E_INVALIDARG</c>；后者需要 COM interop +
/// FOLDERID 映射，面积更大却仍要读同一份 <c>desktop.ini</c>。直接按文件解析在
/// net8 与 net48 上都是纯 BCL + 一个 P/Invoke，且不依赖目录属性是否完好。
/// </para>
/// <para>
/// 解析不到（没文件 / 没这个键 / 间接串失效 / 名字不能做目录名）时返回 <c>null</c>，
/// 由调用方回退到源路径最后一段的<b>物理名</b>——那是语言无关的行为，不会凭空造名字。
/// </para>
/// </remarks>
public static class FolderDisplayName
{
    private const string DesktopIniName = "desktop.ini";

    private const string ShellClassInfoSection = "[.ShellClassInfo]";

    private const string LocalizedResourceKey = "LocalizedResourceName";

    private const int MaxResourceChars = 1024;

    /// <summary>
    /// 取源文件夹的本地化显示名；取不到返回 <c>null</c>。
    /// </summary>
    /// <param name="path">源目录绝对路径（展开后的当前位置）。</param>
    /// <remarks>
    /// 不设"注入钩子"这类静态可写状态：名字来自磁盘上真实存在的 <c>desktop.ini</c>，
    /// 单测直接构造临时目录 + 文件即可覆盖全部路径。静态钩子在 xUnit 并行测试之间会串扰，
    /// 而它带来的唯一价值（避免读文件）在这里并不需要。
    /// </remarks>
    public static string? Resolve(string path)
    {
        string? resource = ReadRawResourceName(path);
        if (resource is null || string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        string localized = resource.StartsWith("@", StringComparison.Ordinal)
            ? ResolveIndirectString(resource)
            : resource;

        return IsValidDirectoryName(localized) ? localized : null;
    }

    /// <summary>
    /// 读源目录 <c>desktop.ini</c> 里<b>原始</b>的 <c>LocalizedResourceName</c> 值。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Resolve"/> 的区别：这里不解析 <c>@module,-id</c> 间接串。
    /// 搬到目标目录后必须写回<b>原始串</b>，才能让资源管理器按本机 UI 语言显示名称；
    /// 写死解析后的某种语言文本会把别的语言的机器改错。
    /// </remarks>
    /// <param name="path">源目录绝对路径。</param>
    /// <returns>原始资源串（可能是间接串）；没有或读失败返回 <c>null</c>。</returns>
    public static string? ReadRawResourceName(string path)
    {
        if (!Compat.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return ReadLocalizedResourceValue(path);
    }

    /// <summary>
    /// 读 <c>desktop.ini</c> 的 <c>[.ShellClassInfo]</c> 段里的 <c>LocalizedResourceName</c>。
    /// 任何读失败都当作"没有这个名字"（不是错误，调用方会回退到 Windows 默认显示名）。
    /// </summary>
    private static string? ReadLocalizedResourceValue(string path)
    {
        try
        {
            string ini = PathTools.ToExtended(
                Path.Combine(PathTools.StripExtendedPrefix(path), DesktopIniName));
            if (!File.Exists(ini))
            {
                return null;
            }

            // File.ReadAllLines 用 UTF-8 + BOM 探测：系统生成的 desktop.ini 常是
            // UTF-16LE with BOM，StreamReader 会据 BOM 自动切换；两个目标框架行为一致。
            string[] lines = File.ReadAllLines(ini);
            bool inSection = false;
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 ||
                    trimmed.StartsWith(";", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    inSection = string.Equals(
                        trimmed,
                        ShellClassInfoSection,
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                int separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                string key = trimmed.Substring(0, separator).Trim();
                if (!string.Equals(key, LocalizedResourceKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return trimmed.Substring(separator + 1).Trim();
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>解析 <c>@模块,-资源号</c> 形态的间接串；失败返回空串。</summary>
    private static string ResolveIndirectString(string indirect)
    {
        try
        {
            var buffer = new StringBuilder(MaxResourceChars);
            int hr = NativeMethods.SHLoadIndirectString(
                indirect,
                buffer,
                (uint)buffer.Capacity,
                IntPtr.Zero);
            return hr == 0 ? buffer.ToString() : string.Empty;
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or System.ComponentModel.Win32Exception)
        {
            // shlwapi 不可用 / 调用失败：按"解析不到"处理，绝不退化成某种硬编码名字。
            return string.Empty;
        }
    }

    /// <summary>
    /// 把本地化显示名资源写到目标目录的 <c>desktop.ini</c>，并设置 Shell 识别所需的属性。
    /// </summary>
    /// <remarks>
    /// <b>这里只改显示，不改名。</b>磁盘目录名与注册表路径仍是调用方给出的英文物理路径；
    /// 资源管理器/快速访问通过 <c>desktop.ini</c> 的 <c>LocalizedResourceName</c> 显示中文。
    /// <para>
    /// 写回的是源目录里的<b>原始资源串</b>（可能是 <c>@module,-id</c>），不是解析后的某种语言文本，
    /// 因此换一台不同 UI 语言的机器也能正确显示。已有 <c>desktop.ini</c> 的其它段与该键之外的
    /// 行会原样保留，只替换/插入 <c>LocalizedResourceName</c>。
    /// </para>
    /// </remarks>
    /// <param name="directoryPath">目标目录（英文物理路径）。</param>
    /// <param name="rawResourceName">源目录里读到的原始显示名资源串。</param>
    public static OpResult ApplyToDirectory(string directoryPath, string rawResourceName)
    {
        if (!Compat.IsWindows())
        {
            return new OpResult(true, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(rawResourceName))
        {
            return new OpResult(false, "目标目录或显示名资源为空");
        }

        if (!IsValidResourceValue(rawResourceName))
        {
            return new OpResult(false, "显示名资源含非法字符或过长");
        }

        try
        {
            string directory = PathTools.ToExtended(PathTools.StripExtendedPrefix(directoryPath.Trim()));
            if (!Directory.Exists(directory))
            {
                return new OpResult(false, $"目标目录不存在：{directoryPath}");
            }

            string ini = PathTools.ToExtended(
                Path.Combine(PathTools.StripExtendedPrefix(directory), DesktopIniName));
            Encoding encoding = DetectEncoding(ini);
            string content = File.Exists(ini) ? File.ReadAllText(ini, encoding) : string.Empty;

            File.WriteAllText(ini, UpsertLocalizedResourceName(content, rawResourceName), encoding);
            // Shell 只在目录带 System 属性时才使用 desktop.ini；ini 自身必须是隐藏系统文件。
            File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
            var directoryInfo = new DirectoryInfo(directory);
            if ((directoryInfo.Attributes & FileAttributes.System) == 0)
            {
                directoryInfo.Attributes |= FileAttributes.System;
            }

            NotifyShell();
            return new OpResult(true, string.Empty);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return new OpResult(false, ex.Message);
        }
    }

    /// <summary>探测已有 <c>desktop.ini</c> 的编码，写回时保持原编码；新文件用带 BOM 的 UTF-8。</summary>
    private static Encoding DetectEncoding(string path)
    {
        if (!File.Exists(path))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        var head = new byte[4];
        int read;
        using (var stream = new FileStream(
            PathTools.ToExtended(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.Read(head, 0, head.Length);
        }

        if (read >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (read >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (read >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    /// <summary>只替换/插入 <c>[.ShellClassInfo]</c> 里的 LocalizedResourceName，其余行原样保留。</summary>
    private static string UpsertLocalizedResourceName(string content, string value)
    {
        var lines = new List<string>(
            content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        int section = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), ShellClassInfoSection, StringComparison.OrdinalIgnoreCase))
            {
                section = i;
                break;
            }
        }

        if (section < 0)
        {
            lines.Add(ShellClassInfoSection);
            lines.Add(LocalizedResourceKey + "=" + value);
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        int sectionEnd = lines.Count;
        for (int i = section + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                sectionEnd = i;
                break;
            }
        }

        bool replaced = false;
        for (int i = section + 1; i < sectionEnd; i++)
        {
            int separator = lines[i].IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = lines[i].Substring(0, separator).Trim();
            if (string.Equals(key, LocalizedResourceKey, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = LocalizedResourceKey + "=" + value;
                replaced = true;
            }
        }

        if (!replaced)
        {
            lines.Insert(section + 1, LocalizedResourceKey + "=" + value);
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>间接串可以含 <c>\</c> <c>:</c> <c>,</c>；字面量仍按目录名规则校验。</summary>
    private static bool IsValidResourceValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxResourceChars)
        {
            return false;
        }

        if (value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
        {
            return false;
        }

        return value.StartsWith("@", StringComparison.Ordinal) || IsValidDirectoryName(value);
    }

    /// <summary>通知 Shell 重新读取文件夹显示名，避免快速访问继续用旧缓存。</summary>
    private static void NotifyShell()
    {
        try
        {
            NativeMethods.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
            // 显示刷新失败不影响数据正确性，下一次打开资源管理器仍会重新读取。
        }
    }

    /// <summary>名字能否直接用作 Windows 目录名（不含路径分隔符、非法字符、保留形态）。</summary>
    private static bool IsValidDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
        {
            return false;
        }

        if (name == "." || name == ".." || name != name.Trim())
        {
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        // GetInvalidFileNameChars 在 Windows 上已含这些字符；这里再挡一次，
        // 保证任何运行时/平台上都不会把名字拼成多级路径。
        return name.IndexOf('\\') < 0 && name.IndexOf('/') < 0 && name.IndexOf(':') < 0;
    }
}
