using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vigilo.App.Services;

internal static class WindowsTaskbarIdentity
{
    internal const string AppUserModelId = "TomMesani.Vigilo";

    private const int ApplicationIconResourceId = 32512;
    private const ushort VariantTypeEmpty = 0;
    private const ushort VariantTypeString = 31;

    private static readonly Guid PropertyStoreInterfaceId = typeof(IPropertyStore).GUID;
    private static readonly PropertyKey AppUserModelIdKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        5);
    private static readonly PropertyKey RelaunchCommandKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        2);
    private static readonly PropertyKey RelaunchIconResourceKey = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        3);

    internal static void Apply(Window window)
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The Vigilo executable path is unavailable.");
        var windowHandle = new WindowInteropHelper(window).EnsureHandle();
        var propertyStore = GetPropertyStore(windowHandle);

        try
        {
            SetString(propertyStore, RelaunchCommandKey, $"\"{executablePath}\"");
            SetString(
                propertyStore,
                RelaunchIconResourceKey,
                $"{executablePath},-{ApplicationIconResourceId}");
            SetString(propertyStore, AppUserModelIdKey, AppUserModelId);
        }
        finally
        {
            Marshal.FinalReleaseComObject(propertyStore);
        }
    }

    internal static void Clear(Window window)
    {
        var windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var propertyStore = GetPropertyStore(windowHandle);
        try
        {
            ClearValue(propertyStore, AppUserModelIdKey);
            ClearValue(propertyStore, RelaunchCommandKey);
            ClearValue(propertyStore, RelaunchIconResourceKey);
        }
        finally
        {
            Marshal.FinalReleaseComObject(propertyStore);
        }
    }

    private static IPropertyStore GetPropertyStore(IntPtr windowHandle)
    {
        var interfaceId = PropertyStoreInterfaceId;
        Marshal.ThrowExceptionForHR(
            SHGetPropertyStoreForWindow(windowHandle, ref interfaceId, out var propertyStore));
        return propertyStore;
    }

    private static void SetString(IPropertyStore propertyStore, PropertyKey key, string value)
    {
        var propertyValue = PropVariant.FromString(value);
        try
        {
            Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, ref propertyValue));
        }
        finally
        {
            propertyValue.Dispose();
        }
    }

    private static void ClearValue(IPropertyStore propertyStore, PropertyKey key)
    {
        var propertyValue = new PropVariant { VariantType = VariantTypeEmpty };
        Marshal.ThrowExceptionForHR(propertyStore.SetValue(ref key, ref propertyValue));
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(
        IntPtr windowHandle,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propertyValue);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint propertyCount);

        [PreserveSig]
        int GetAt(uint propertyIndex, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant : IDisposable
    {
        [FieldOffset(0)]
        public ushort VariantType;

        [FieldOffset(8)]
        public IntPtr PointerValue;

        public static PropVariant FromString(string value) => new()
        {
            VariantType = VariantTypeString,
            PointerValue = Marshal.StringToCoTaskMemUni(value)
        };

        public void Dispose()
        {
            if (VariantType != VariantTypeEmpty)
            {
                Marshal.ThrowExceptionForHR(PropVariantClear(ref this));
            }
        }
    }
}
