using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ShaiTu {
    public sealed class EdgeHandleWindow : Window {
        public readonly Border Grip;
        readonly DispatcherTimer dwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        bool blocked;
        readonly Func<System.Drawing.Point> cursor;
        public IntPtr Handle;
        public event Action<Forms.Screen> OpenRequested;
        public EdgeHandleWindow(Func<System.Drawing.Point> pointer = null) {
            cursor = pointer ?? delegate { return Forms.Cursor.Position; };
            Title = "晒图边缘把手"; Icon = AppIcons.WindowIcon;
            Width = 18; Height = 64; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
            ShowInTaskbar = false; ShowActivated = false;
            Grip = new Border { Background = LineWindow.Brush("#EEF0F3F7"), BorderBrush = LineWindow.Brush("#9DA7B4"),
                BorderThickness = new Thickness(1, 1, 0, 1), CornerRadius = new CornerRadius(9, 0, 0, 9), Cursor = Cursors.Hand,
                Child = new TextBlock { Text = "‹", FontSize = 22, Foreground = LineWindow.Brush("#536173"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            Content = Grip; Opacity = .72;
            MouseEnter += delegate { Fade(1); if (!blocked) dwell.Start(); };
            MouseLeave += delegate { Fade(.72); blocked = false; dwell.Stop(); };
            Grip.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { e.Handled = true; Open(); };
            dwell.Tick += delegate { dwell.Stop(); if (IsVisible && !blocked && Region().Contains(cursor())) Open(); };
            SourceInitialized += delegate {
                Handle = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(Handle, -20, Native.GetWindowLong(Handle, -20) | 0x08000000 | 0x80);
                HwndSource.FromHwnd(Handle).AddHook(delegate(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) {
                    if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(3); }
                    return IntPtr.Zero;
                });
            };
        }
        void Fade(double target) {
            double from = Opacity; BeginAnimation(OpacityProperty, null); Opacity = target;
            BeginAnimation(OpacityProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(120)) { FillBehavior = FillBehavior.Stop });
        }
        void Open() {
            dwell.Stop();
            if (OpenRequested != null) OpenRequested(Forms.Screen.FromHandle(Handle));
        }
        public System.Drawing.Rectangle Region() {
            Native.RECT r; return Handle != IntPtr.Zero && Native.GetWindowRect(Handle, out r)
                ? new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : System.Drawing.Rectangle.Empty;
        }
        public void Sync(bool visible, Forms.Screen screen, IntPtr lineHandle) {
            if (!visible) { dwell.Stop(); if (IsVisible) Hide(); return; }
            if (Handle == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
            var source = HwndSource.FromHwnd(Handle);
            for (int pass = 0; pass < 2; pass++) {
                double scale = source.CompositionTarget.TransformToDevice.M22;
                Native.RECT line; Native.GetWindowRect(lineHandle, out line);
                int width = (int)Math.Round(18 * scale), height = (int)Math.Round(64 * scale);
                int y = Math.Min(screen.WorkingArea.Bottom - height, line.Top + (int)Math.Round(26 * scale));
                Native.SetWindowPos(Handle, new IntPtr(-1), screen.WorkingArea.Right - width, Math.Max(screen.WorkingArea.Top, y), width, height, 0x10);
            }
            if (!IsVisible) {
                blocked = Region().Contains(cursor()); Show();
                if (blocked) dwell.Stop();
            }
        }
        public void Stop() { dwell.Stop(); Close(); }
    }
    public sealed class PointerDismissal {
        DateTime away = DateTime.MinValue;
        public bool Update(DateTime now, bool inside, bool hold) {
            if (inside || hold) { Reset(); return false; }
            if (away == DateTime.MinValue) away = now;
            return (now - away).TotalMilliseconds >= 1200;
        }
        public void Reset() { away = DateTime.MinValue; }
    }
}
