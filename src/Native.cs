using System;
using System.Runtime.InteropServices;

namespace ValeraScreenshot
{
    // Win32 interop: метрики віртуального екрана, GDI-захоплення, курсор, глобальні гарячі клавіші, DPI.
    internal static class Native
    {
        // --- віртуальний екран (фізичні пікселі, коли процес DPI-aware) ---
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);

        // --- GDI-захоплення ---
        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000; // включно з layered-вікнами

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                         IntPtr hdcSrc, int sx, int sy, int rop);

        // --- курсор (опційне вмальовування у знімок) ---
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        public const int CURSOR_SHOWING = 1;
        public const int DI_NORMAL = 3;

        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO pii);
        [DllImport("user32.dll")]
        public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon,
                                             int cx, int cy, int steps, IntPtr brush, int flags);

        // --- глобальні гарячі клавіші ---
        public const int WM_HOTKEY = 0x0312;

        // Другий запуск показує перший, а не показує нотацію. Ім'я повідомлення реєструється в
        // системі, тож воно однакове в обох процесах і унікальне для нашої айдентики;
        // HWND_BROADCAST потрібен, бо вікно-приймач у першого екземпляра приховане й ми не
        // знаємо його хендла.
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        public const int MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        public const int VK_SNAPSHOT = 0x2C; // PrtScr

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, int mods, int vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // --- DPI: PerMonitorV2 уже в маніфесті; виклик тут — страховка для tools/tests без маніфеста ---
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

        public static void EnsureDpiAware()
        {
            try { if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return; }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }
    }
}
