using System;
using System.Runtime.InteropServices;

namespace ShaiTu {
    internal static class Native {
        public const int WM_CLIPBOARDUPDATE = 0x31D, WM_HOTKEY = 0x312, WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int count);
        public static bool IsDesktopClass(string name) { return name == "Progman" || name == "WorkerW"; }
        public static bool IsDesktopAtPoint(System.Drawing.Point point, IntPtr overlay) {
            // Inspect the visible window beneath our overlay, rather than its topmost HWND.
            IntPtr hwnd = GetTopWindow(IntPtr.Zero);
            for (int i = 0; hwnd != IntPtr.Zero && i < 512; i++, hwnd = GetWindow(hwnd, 2)) {
                if (hwnd == overlay || !IsWindowVisible(hwnd) || IsIconic(hwnd)) continue;
                int cloaked; if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) continue;
                RECT r; if (!GetWindowRect(hwnd, out r) || point.X < r.Left || point.X >= r.Right || point.Y < r.Top || point.Y >= r.Bottom) continue;
                var name = new System.Text.StringBuilder(128); GetClassName(hwnd, name, name.Capacity);
                return IsDesktopClass(name.ToString());
            }
            return false;
        }
        public static string ClipboardOwnerName() {
            try {
                uint pid; GetWindowThreadProcessId(GetClipboardOwner(), out pid);
                if (pid == 0) return "unknown";
                using (var process = System.Diagnostics.Process.GetProcessById((int)pid)) return process.ProcessName;
            } catch { return "unknown"; }
        }
        public static bool IsFullscreen(System.Windows.Forms.Screen screen) {
            IntPtr fg = GetForegroundWindow(); RECT r;
            if (fg == IntPtr.Zero || !GetWindowRect(fg, out r)) return false;
            if (IsZoomed(fg) && (GetWindowLong(fg, -16) & 0x00C00000) != 0) return false;
            var name = new System.Text.StringBuilder(128); GetClassName(fg, name, name.Capacity);
            if (name.ToString() == "Progman" || name.ToString() == "WorkerW") return false;
            var b = screen.Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }
    }
}
