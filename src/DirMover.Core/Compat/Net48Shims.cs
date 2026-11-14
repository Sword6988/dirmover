#if NET48
using System;

namespace DirMover.Core;

/// <summary>
/// net48 兼容垫片：只在本目标上编译，net8 不参与。
/// </summary>
/// <remarks>
/// 为什么不是"在调用点写 #if"：
/// <list type="bullet">
/// <item>Core 里有近 20 处 <c>ArgumentNullException.ThrowIfNull</c>、1 处 <c>HashCode.Combine</c>。
/// 每处都加 <c>#if</c> 会让同一段安全代码在两个目标下长得不一样，审计时"看到的"不等于"编出来的"。</item>
/// <item>这两个类型只在 <see cref="DirMover.Core"/> 及其子命名空间内被使用，
/// 在这里声明一个派生/同名类型，命名空间查找优先于 <c>using System;</c>，
/// 调用点因此一个字都不用改；而 shim 派生自 BCL 原类型，语义完全一致。</item>
/// </list>
/// 代价：本命名空间内的 <c>new ArgumentNullException(...)</c> 构造的是 shim 子类。
/// 它 <b>是</b> <see cref="System.ArgumentNullException"/>，任何 catch / is 判断都不受影响。
/// </remarks>
internal sealed class ArgumentNullException : System.ArgumentNullException
{
    public ArgumentNullException()
    {
    }

    public ArgumentNullException(string? paramName) : base(paramName)
    {
    }

    public ArgumentNullException(string? message, Exception? innerException) : base(message, innerException)
    {
    }

    public ArgumentNullException(string? paramName, string? message) : base(paramName, message)
    {
    }

    /// <summary>net6+ 的静态守卫；null 时抛 <see cref="System.ArgumentNullException"/>。</summary>
    /// <param name="argument">被检查的引用。</param>
    /// <param name="paramName">参数名（编译器自动填入调用处的表达式文本）。</param>
    public static void ThrowIfNull(
        object? argument,
        [System.Runtime.CompilerServices.CallerArgumentExpression("argument")] string? paramName = null)
    {
        if (argument is null)
        {
            throw new System.ArgumentNullException(paramName);
        }
    }
}

/// <summary>net48 没有 <see cref="System.HashCode"/>，这里补一个签名一致的最小实现。</summary>
internal static class HashCode
{
    /// <summary>两值组合哈希；不必与 net8 逐位相同（只要求等值对象同哈希）。</summary>
    public static int Combine<T1, T2>(T1 value1, T2 value2)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + (value1 is null ? 0 : value1.GetHashCode());
            hash = (hash * 31) + (value2 is null ? 0 : value2.GetHashCode());
            return hash;
        }
    }
}
#endif
