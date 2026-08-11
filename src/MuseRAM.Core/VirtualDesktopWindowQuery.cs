using System.Runtime.InteropServices;

namespace MuseRAM.Core;

internal sealed class VirtualDesktopWindowQuery : IDisposable
{
    private static readonly Guid ManagerClassId = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private readonly object _comObject;
    private readonly IVirtualDesktopManager _manager;

    private VirtualDesktopWindowQuery(object comObject, IVirtualDesktopManager manager)
    {
        _comObject = comObject;
        _manager = manager;
    }

    internal static VirtualDesktopWindowQuery? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var managerType = Type.GetTypeFromCLSID(ManagerClassId, throwOnError: false);
            var instance = managerType is null ? null : Activator.CreateInstance(managerType);
            return instance is IVirtualDesktopManager manager
                ? new VirtualDesktopWindowQuery(instance, manager)
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal bool IsOnCurrentDesktopOrUnknown(IntPtr window)
    {
        try
        {
            var result = _manager.IsWindowOnCurrentVirtualDesktop(window, out var isOnCurrentDesktop);
            return result != 0 || isOnCurrentDesktop;
        }
        catch
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (Marshal.IsComObject(_comObject)) Marshal.FinalReleaseComObject(_comObject);
        }
        catch
        {
            // A failed cleanup must not invalidate an otherwise reliable process sample.
        }
    }

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(
            IntPtr topLevelWindow,
            [MarshalAs(UnmanagedType.Bool)] out bool isOnCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, in Guid desktopId);
    }
}
