using System.Runtime.InteropServices;

namespace TuringDesk.Desktop.Services.AppSearch;

internal static class PackagedAppLauncher
{
    public static bool TryLaunch(string appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId)) return false;

        IApplicationActivationManager? manager = null;
        try
        {
            manager = (IApplicationActivationManager)new ApplicationActivationManager();
            var hr = manager.ActivateApplication(appUserModelId, null, ActivateOptions.None, out _);
            Marshal.ThrowExceptionForHR(hr);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
            {
                try { _ = Marshal.FinalReleaseComObject(manager); } catch { }
            }
        }
    }

    [Flags]
    private enum ActivateOptions : uint
    {
        None = 0
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ActivateOptions options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;
}
