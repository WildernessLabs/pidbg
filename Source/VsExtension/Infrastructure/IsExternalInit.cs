// Polyfill required for positional records and init-only setters on net472.
// The compiler emits references to this type; it must exist at compile time.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
