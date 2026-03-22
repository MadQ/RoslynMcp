namespace RoslynMcp.Tests;

/// <summary>
/// Test file to demonstrate the PreferNintOverIntPtrAnalyzer.
/// This should produce warnings for IntPtr and UIntPtr usage.
/// </summary>
internal static class AnalyzerDemo
{
    // This should trigger RMCP001: Prefer 'nint' over 'IntPtr'
    public static IntPtr GetHandle()
    {
        return IntPtr.Zero;
    }

    // This is the preferred style (no warning)
    public static nint GetHandlePreferred()
    {
        return 0;
    }

    // This should also trigger RMCP001
    public static void ProcessHandle(IntPtr handle)
    {
        if(handle == IntPtr.Zero)
            return;
    }

    // This should trigger RMCP002: Prefer 'nuint' over 'UIntPtr'
    public static UIntPtr GetUnsignedHandle()
    {
        return UIntPtr.Zero;
    }

    // This is the preferred style (no warning)
    public static nuint GetUnsignedHandlePreferred()
    {
        return 0;
    }

    // This should also trigger RMCP002
    public static void ProcessUnsignedHandle(UIntPtr handle)
    {
        if(handle == UIntPtr.Zero)
            return;
    }
}
