// Polyfill attributes needed by C# 9+ features when targeting
// netstandard2.1 / net standard 2.0 — init-only setters, the `required`
// keyword, and related compiler intrinsics. These types ship in the BCL
// starting with .NET 5 / .NET 7, so the shim is only needed for the
// netstandard2.1 leg of our multi-target build.
//
// Keep the shim compile-only: `internal` and `ConditionalAttribute` gated
// so it doesn't leak into consumer APIs, and `#if !NET5_0_OR_GREATER` /
// equivalent guards so newer TFMs pick up the BCL copies.

#if NETSTANDARD2_0 || NETSTANDARD2_1

namespace System.Runtime.CompilerServices
{
    // Required for `init` accessors (C# 9).
    internal static class IsExternalInit { }

    // Required for the `required` keyword (C# 11).
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) { FeatureName = featureName; }
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }

    // Trim/AOT annotation shim (ships in .NET 5+). Used to tell the trim
    // analyzer which members of a generic type parameter must be kept alive.
    // No-op at runtime — the attribute is only consumed by the analyzer.
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface |
        AttributeTargets.Delegate | AttributeTargets.Method | AttributeTargets.Constructor |
        AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.ReturnValue |
        AttributeTargets.GenericParameter | AttributeTargets.Parameter,
        Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes)
            => MemberTypes = memberTypes;
        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }

    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0003,
        NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008,
        NonPublicMethods = 0x0010,
        PublicFields = 0x0020,
        NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080,
        NonPublicNestedTypes = 0x0100,
        PublicProperties = 0x0200,
        NonPublicProperties = 0x0400,
        PublicEvents = 0x0800,
        NonPublicEvents = 0x1000,
        Interfaces = 0x2000,
        All = ~None,
    }
}

#endif
