using System.Runtime.InteropServices;

namespace Sitepane;

/// <summary>Win32/DWM plumbing for a caption-less window that keeps the native Win11 frame.</summary>
internal static class NativeChrome
{
    public const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_SETICON = 0x0080;

    public const int WS_CAPTION = 0x00C00000;
    public const int WS_SYSMENU = 0x00080000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;

    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const int SM_CXICON = 11;
    private const int SM_CXSMICON = 49;
    private const int SM_SWAPBUTTON = 23;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0, rgrc1, rgrc2;
        public IntPtr lppos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    public static void ApplyWindowAttributes(IntPtr hwnd)
    {
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        // Caption-less windows are not rounded by default; Win11 never rounds maximized windows.
        int corners = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
    }

    /// <summary>
    /// WM_NCCALCSIZE: the whole window is client area (no caption, no visible frame). A maximized
    /// window overhangs its monitor by the frame width, so its client is clamped to the work area.
    /// Uses IsZoomed: WinForms' WindowState is only updated at WM_SIZE, after this message, so it is
    /// stale during maximize/restore transitions (the source of the stray top bar/border).
    /// </summary>
    public static void CalcClientArea(ref Message m)
    {
        m.Result = IntPtr.Zero;
        if (m.WParam == IntPtr.Zero || !IsZoomed(m.HWnd))
            return;

        var calc = Marshal.PtrToStructure<NCCALCSIZE_PARAMS>(m.LParam);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromRect(ref calc.rgrc0, MONITOR_DEFAULTTONEAREST), ref info))
            return;

        calc.rgrc0 = info.rcWork;
        Marshal.StructureToPtr(calc, m.LParam, fDeleteOld: false);
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Hands an in-progress mouse press over to the native move/size loop for the given edge.</summary>
    public static void BeginResize(IntPtr hwnd, int hitTest)
    {
        // The page can post at any time; only start sizing while the primary button is physically held.
        if (!IsKeyDown(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON))
            return;

        GetCursorPos(out var pt);
        ReleaseCapture();
        PostMessage(hwnd, WM_NCLBUTTONDOWN, hitTest, (pt.y << 16) | (pt.x & 0xFFFF));
    }

    /// <summary>Taskbar/Alt-Tab (big) and caption (small) icon sizes for the window's current DPI.</summary>
    public static (int Big, int Small) IconSizes(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return (GetSystemMetricsForDpi(SM_CXICON, dpi), GetSystemMetricsForDpi(SM_CXSMICON, dpi));
    }

    public static void SetWindowIcons(IntPtr hwnd, Icon big, Icon small)
    {
        SendMessage(hwnd, WM_SETICON, 1, big.Handle);
        SendMessage(hwnd, WM_SETICON, 0, small.Handle);
    }
}
