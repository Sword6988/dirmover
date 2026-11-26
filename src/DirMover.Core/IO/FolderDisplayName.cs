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
        if (!Compat.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string? resource = ReadLocalizedResourceName(path);
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
    /// 读 <c>desktop.ini</c> 的 <c>[.ShellClassInfo]</c> 段里的 <c>LocalizedResourceName</c>。
    /// 任何读失败都当作"没有这个名字"（不是错误，调用方会回退物理名）。
    /// </summary>
    private static string? ReadLocalizedResourceName(string path)
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
