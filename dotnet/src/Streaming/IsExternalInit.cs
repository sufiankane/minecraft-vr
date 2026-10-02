// netstandard2.1 has no IsExternalInit; C# 10 records need it for init-only members.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
