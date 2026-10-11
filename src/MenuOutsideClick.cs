using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ShaiTu {
    // The non-activating overlay cannot rely on losing foreground focus to dismiss its menu.
    // Listen only while a menu is open, and always forward the click to its intended target.
    internal sealed class MenuOutsideClick : IDisposable {
        readonly ContextMenu menu;
        readonly MouseProc callback;
        IntPtr hook;
        bool disposed, queued;
        internal bool Watching { get { return hook != IntPtr.Zero; } }
        internal MenuOutsideClick(ContextMenu target) {
            menu = target; callback = MouseEvent;
            hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听菜单外点击。");
        }
        IntPtr MouseEvent(int code, IntPtr message, IntPtr data) {
            if (code >= 0 && message.ToInt32() == 0x0201) {
                // Keep the hook short: UI changes are queued after forwarding the input.
                LeftButtonDown(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4));
            }
            return CallNextHookEx(hook, code, message, data);
        }
        internal void LeftButtonDown(int x, int y) {
            if (disposed || queued || !menu.IsOpen) return;
            var source = PresentationSource.FromVisual(menu) as HwndSource;
            Native.RECT rect;
            if (source == null || !Native.GetWindowRect(source.Handle, out rect)) return;
            if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom) return;
            queued = true;
            menu.Dispatcher.BeginInvoke(new Action(delegate {
                if (!disposed && menu.IsOpen) menu.IsOpen = false;
            }), DispatcherPriority.Input);
        }
        public void Dispose() {
            if (disposed) return; disposed = true;
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            GC.KeepAlive(callback);
        }
        delegate IntPtr MouseProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, MouseProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    }
}
