using System.Runtime.InteropServices;
using Daggeragent.Server;
using Microsoft.Extensions.Logging;

namespace Daggeragent.Modes;

/// <summary>
/// Sets the console window icon from the embedded icon.ico via Win32 calls.
/// No-op on non-Windows platforms or when stdout isn't attached to a console.
/// </summary>
/// <remarks>
/// Two paths are attempted: WM_SETICON (per-window) and SetClassLongPtr (per window-class).
/// Some Windows shells respect one but not the other. Note that Windows Terminal renders
/// each tab via its own UI process and does NOT pick up either signal from the underlying
/// console window — the tab icon there comes from the Windows Terminal profile, not the
/// hosted application. In legacy conhost (cmd.exe directly, or "Open in Windows Console
/// Host" on Windows 11), the title-bar and Alt-Tab icon get set correctly.
/// </remarks>
internal static class ConsoleIcon
{
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const uint LR_DEFAULTSIZE = 0x40;
    private const uint WM_SETICON = 0x80;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    public static void TrySet(ILogger? log = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            log?.LogDebug("ConsoleIcon: skipped (not Windows)");
            return;
        }

        try
        {
            var hwnd = GetConsoleWindow();
            if (hwnd == IntPtr.Zero)
            {
                log?.LogDebug("ConsoleIcon: GetConsoleWindow returned 0 (no attached console)");
                return;
            }

            var iconPath = EmbeddedAssets.GetIconTempPath();
            if (iconPath is null)
            {
                log?.LogWarning("ConsoleIcon: EmbeddedAssets.GetIconTempPath returned null — icon not extracted");
                return;
            }

            var hSmall = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            var hBig = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (hSmall == IntPtr.Zero && hBig == IntPtr.Zero)
            {
                log?.LogWarning("ConsoleIcon: LoadImage failed for both sizes (err={Err}, path={Path})",
                    Marshal.GetLastWin32Error(), iconPath);
                return;
            }

            // Path 1: WM_SETICON — works in classic conhost.
            if (hSmall != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, hSmall);
            if (hBig != IntPtr.Zero)   SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, hBig);

            // Path 2: SetClassLongPtr — some shells (and the Alt-Tab thumbnail) read the icon
            // from the window class rather than the per-window message. Setting both is harmless
            // when one is enough.
            if (hSmall != IntPtr.Zero) SetClassLongPtr(hwnd, GCLP_HICONSM, hSmall);
            if (hBig != IntPtr.Zero)   SetClassLongPtr(hwnd, GCLP_HICON, hBig);

            var inWindowsTerminal = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));
            log?.LogInformation("ConsoleIcon: set (hwnd=0x{Hwnd:X}, hSmall=0x{HSmall:X}, hBig=0x{HBig:X}, terminal={Term})",
                hwnd.ToInt64(), hSmall.ToInt64(), hBig.ToInt64(),
                inWindowsTerminal ? "Windows Terminal (tab icon ignores app — set the WT profile icon instead)" : "conhost");
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "ConsoleIcon: TrySet threw");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // SetClassLongPtr is 64-bit/32-bit name-mangled. On x64 use SetClassLongPtrW; on x86
    // there's no Ptr variant — SetClassLongW does the same job with the narrower type. The
    // CLR's P/Invoke resolves the correct one via the entry-point name.
    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "SetClassLongW", SetLastError = true)]
    private static extern uint SetClassLong32(IntPtr hWnd, int nIndex, uint dwNewLong);
    private static IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8 ? SetClassLongPtr64(hWnd, nIndex, dwNewLong)
                         : new IntPtr((int)SetClassLong32(hWnd, nIndex, (uint)dwNewLong.ToInt32()));
}
