#if !NET5_0_OR_GREATER
// Polyfill so that C# 9 `init` accessors and records compile for netstandard2.1 (Unity). The runtime never
// looks at this type; the compiler only needs it to exist as a modreq marker.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
