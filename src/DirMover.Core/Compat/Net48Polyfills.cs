#if NET48
// C# 9/11 的 record、init、required 需要运行时类型。.NET Framework 4.8 的 BCL 没有它们，
// 编译器只要求"存在同名同形状的类型"，因此按官方文档在本地补齐（不复制实现）。
// 参考：https://learn.microsoft.com/dotnet/standard/assembly/polyfill-csharp-features

namespace System.Runtime.CompilerServices
{
    /// <summary>init 访问器所需的标记类型。</summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>required 成员所需的标记特性。</summary>
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>required 语法触发的编译器特性声明。</summary>
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
        {
            FeatureName = featureName;
        }

        public string FeatureName { get; }

        public bool IsOptional { get; init; }

        public const string RefStructs = nameof(RefStructs);

        public const string RequiredMembers = nameof(RequiredMembers);
    }

    /// <summary>
    /// net6+ 的 <c>CallerArgumentExpression</c>；用于让 <c>ArgumentNullException.ThrowIfNull</c>
    /// 这类 shim 自动拿到调用处的实参表达式文本（.NET Framework 4.8 的 BCL 没有它）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName)
        {
            ParameterName = parameterName;
        }

        public string ParameterName { get; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>构造函数已设置全部 required 成员时使用的标记特性。</summary>
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
#endif
