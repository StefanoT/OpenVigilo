using System.Runtime.InteropServices;

namespace Vigilo.Outlook;

internal sealed class RunningOutlookApplicationProvider
{
    public object GetRunningApplication()
    {
        if (!OperatingSystem.IsWindows())
            throw new OutlookUnavailableException(Vigilo.Core.OutlookErrorCodes.UnsupportedPlatform, "Outlook category tagging requires Windows.");

        var type = Type.GetTypeFromProgID("Outlook.Application", throwOnError: false);
        if (type is null)
            throw new OutlookUnavailableException(Vigilo.Core.OutlookErrorCodes.ClassicNotInstalled, "Classic Outlook is not installed.");

        var clsid = type.GUID;
        var result = GetActiveObject(ref clsid, IntPtr.Zero, out var application);
        if (result == unchecked((int)0x800401E3) || application is null)
            throw new OutlookUnavailableException(Vigilo.Core.OutlookErrorCodes.NotRunning, "Classic Outlook is not running.");
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        return application;
    }

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid rclsid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object? ppunk);
}

internal sealed class OutlookUnavailableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
