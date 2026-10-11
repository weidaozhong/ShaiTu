using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ShaiTu {
    public sealed class LineWindow : Window {
        public readonly Canvas Surface = new Canvas();
        readonly Store store;
        readonly TranslateTransform slide = new TranslateTransform();
        readonly Action<Exception> error;
        public bool Dragging, Busy;
        public bool Pinned {
            get { return !store.Settings.AutoHide; }
            set {
                bool previous = store.Settings.AutoHide; store.Settings.AutoHide = !value;
                try { store.Save(); } catch (Exception e) { store.Settings.AutoHide = previous; error(e); }
                if (DisplayModeChanged != null) DisplayModeChanged();
            }
        }
        public bool Revealed { get; private set; }
        public DateTime KeepUntil = DateTime.MinValue;
        public event Action ClipboardUpdated;
        public event Action ToggleRequested;
        public event Action DisplayModeChanged;
        public event Action<Photo> PreviewRequested;
        public IntPtr Handle;
        public uint OwnClipboardSequence;
        public bool HotkeyAvailable;
        public bool ClipboardListenerAvailable;
        ContextMenu photoMenu;
        MenuOutsideClick outsideClick;
        internal MenuOutsideClick MenuWatcher { get { return outsideClick; } }
        bool testMode;
        public LineWindow(Store state, Action<Exception> onError, bool test) {
            store = state; error = onError; testMode = test;
            Title = "晒图 · ShaiTu"; Icon = AppIcons.WindowIcon; Width = 1280; Height = 250;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            Content = Surface; Surface.RenderTransform = slide;
            SizeChanged += delegate { Rebuild(false); };
            store.Changed += delegate { Rebuild(true); };
            Closed += delegate { ClosePhotoMenu(); };
            SourceInitialized += delegate {
                Handle = new WindowInteropHelper(this).Handle;
                if (testMode) return;
                int style = Native.GetWindowLong(Handle, -20);
                Native.SetWindowLong(Handle, -20, style | 0x08000000 | 0x80); // NOACTIVATE, TOOLWINDOW
                HwndSource.FromHwnd(Handle).AddHook(Hook);
                ClipboardListenerAvailable = Native.AddClipboardFormatListener(Handle);
                if (!ClipboardListenerAvailable) error(new InvalidOperationException("无法监听剪贴板，请重新启动晒图。"));
                HotkeyAvailable = Native.RegisterHotKey(Handle, 1, 0x4000 | store.Settings.HotkeyModifiers, store.Settings.HotkeyKey);
            };
            Rebuild(false);
        }
        IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) {
            if (msg == Native.WM_CLIPBOARDUPDATE && ClipboardUpdated != null) ClipboardUpdated();
            if (msg == Native.WM_HOTKEY && wp.ToInt32() == activeHotkeyId && ToggleRequested != null) ToggleRequested();
            if (msg == Native.WM_MOUSEACTIVATE) { handled = true; return new IntPtr(3); }
            if (msg == Native.WM_NCHITTEST) {
                Native.POINT p; Native.GetCursorPos(out p);
                var local = PointFromScreen(new Point(p.X, p.Y));
                var hit = Surface.InputHitTest(local);
                if (hit == null || hit == Surface) { handled = true; return new IntPtr(-1); }
            }
            return IntPtr.Zero;
        }
        public void Cleanup() {
            ClosePhotoMenu();
            if (Handle != IntPtr.Zero && !testMode) {
                Native.RemoveClipboardFormatListener(Handle); Native.UnregisterHotKey(Handle, activeHotkeyId);
            }
        }
        public bool ChangeHotkey(uint modifiers, uint key, out string failure) {
            failure = "";
            if (!HotkeyBinding.IsValid(modifiers, key)) { failure = "请按下一个有效按键，可同时按 Ctrl、Alt、Shift 或 Win。"; return false; }
            if (HotkeyAvailable && modifiers == store.Settings.HotkeyModifiers && key == store.Settings.HotkeyKey) return true;
            // Reserve a second ID first: a conflicting candidate never releases the working binding.
            int candidateId = activeHotkeyId == 1 ? 2 : 1;
            if (!Native.RegisterHotKey(Handle, candidateId, modifiers | 0x4000, key)) {
                failure = "这个快捷键被其他程序占用或由系统保留，请换一个。原快捷键仍保留。"; return false;
            }
            uint oldModifiers = store.Settings.HotkeyModifiers, oldKey = store.Settings.HotkeyKey;
            store.Settings.HotkeyModifiers = modifiers; store.Settings.HotkeyKey = key;
            try { store.Save(); } catch (Exception ex) {
                store.Settings.HotkeyModifiers = oldModifiers; store.Settings.HotkeyKey = oldKey;
                Native.UnregisterHotKey(Handle, candidateId); failure = "无法保存快捷键：" + ex.Message; return false;
            }
            // Swap IDs rather than unregister/re-register, eliminating a registration race.
            Native.UnregisterHotKey(Handle, activeHotkeyId); activeHotkeyId = candidateId;
            HotkeyAvailable = true; return true;
        }
        int activeHotkeyId = 1;
        public void Reveal(Forms.Screen screen, bool animate) {
            if (store.Items.Count == 0) { Tuck(false); return; }
            Place(screen);
            bool wasShown = Revealed; Revealed = true; Show(); Rebuild(false);
            slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = 0;
            if (animate && !wasShown) slide.BeginAnimation(TranslateTransform.YProperty, Anim(-250, 0, .32));
        }
        public static double ClampOffset(double offset, double workHeight, double scale) {
            return Math.Max(0, Math.Min(offset, Math.Max(0, workHeight / scale - 250)));
        }
        // Match the visible rope/photo envelope, including tilt and shadow, not the 250-DIP HWND.
        public double ActiveBandHeight(double width) {
            double w = Math.Max(width, 400), sag = Math.Min(30, w * .018);
            int count = store.Items.Count;
            if (count == 0) return 0;
            double spacing = Math.Min(188, (w - 64) / count);
            double cardWidth = Math.Min(164, Math.Max(48, spacing - 18));
            double bottom = 12 + sag + 7;
            for (int i = 0; i < count; i++) {
                Photo photo = store.Items[i];
                double x = w / 2 + (i - (count - 1) / 2.0) * spacing;
                double ropeY = 12 + 4 * sag * (x / w) * (1 - x / w);
                double imageHeight = Math.Min((cardWidth - 10) / photo.Width, 112.0 / photo.Height) * photo.Height;
                bottom = Math.Max(bottom, ropeY + 6 + Math.Max(30, imageHeight) + 10 + 14);
            }
            return Math.Ceiling(bottom);
        }
        public System.Drawing.Rectangle ActiveRegion() {
            Native.RECT rect; Native.GetWindowRect(Handle, out rect);
            double scale = HwndSource.FromHwnd(Handle).CompositionTarget.TransformToDevice.M22;
            return new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Right - rect.Left,
                (int)Math.Ceiling(ActiveBandHeight(Width) * scale));
        }
        public System.Drawing.Rectangle KeepRegion() {
            var region = ActiveRegion();
            if (region.Height == 0) return region;
            double scale = HwndSource.FromHwnd(Handle).CompositionTarget.TransformToDevice.M22;
            region.Inflate(0, (int)Math.Ceiling(16 * scale)); return region;
        }
        public void Place(Forms.Screen screen) {
            if (screen != null) {
                // Native placement uses physical pixels; WPF gets the monitor's DPI on WM_DPICHANGED.
                if (Handle == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
                var area = screen.WorkingArea;
                var source = HwndSource.FromHwnd(Handle);
                double scale = source.CompositionTarget.TransformToDevice.M22;
                Width = area.Width / scale;
                int y = area.Top + (int)Math.Round(ClampOffset(store.Settings.HeightOffset, area.Height, scale) * scale);
                Native.SetWindowPos(Handle, new IntPtr(-1), area.Left, y, area.Width, (int)(250 * scale), 0x10);
                // A move to another DPI monitor changes the scale synchronously.
                scale = source.CompositionTarget.TransformToDevice.M22;
                Width = area.Width / scale;
                y = area.Top + (int)Math.Round(ClampOffset(store.Settings.HeightOffset, area.Height, scale) * scale);
                Native.SetWindowPos(Handle, new IntPtr(-1), area.Left, y, area.Width, (int)(250 * scale), 0x10);
            }
        }
        public void Tuck(bool animate) {
            ClosePhotoMenu();
            if (!Revealed) return; Revealed = false;
            if (!animate) { Hide(); return; }
            var a = Anim(0, -250, .20);
            a.Completed += delegate { if (!Revealed) Hide(); };
            slide.BeginAnimation(TranslateTransform.YProperty, a);
        }
        static DoubleAnimation Anim(double from, double to, double seconds) {
            return new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        }
        public static Brush Brush(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }
        static Border Glass(UIElement child, int radius) {
            return new Border { Child = child, CornerRadius = new CornerRadius(radius), Padding = new Thickness(4),
                Background = Brush("#EDF7F9FC"), BorderBrush = Brush("#F8FFFFFF"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .19, BlurRadius = 14, ShadowDepth = 4 } };
        }
        static TextBlock Text(string value, double size, string color) {
            return new TextBlock { Text = value, FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };
        }
        static Button Button(string label, string tip, Action action) {
            var button = new Button { Content = label, ToolTip = tip, FontSize = 12, Padding = new Thickness(10, 6, 10, 6),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brush("#334155"), Cursor = Cursors.Hand };
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.MarginProperty, new Thickness(10, 6, 10, 6));
            factory.AppendChild(content);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
            var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#D9E4EBF2")));
            template.Triggers.Add(trigger); button.Template = template;
            button.Click += delegate { action(); }; return button;
        }
        public void Rebuild(bool animate) {
            ClosePhotoMenu();
            Surface.Children.Clear(); double w = Math.Max(Width, 400);
            int count = store.Items.Count;
            if (count == 0) { Tuck(false); return; }
            var rope = new PathGeometry(); var f = new PathFigure { StartPoint = new Point(-20, 12) };
            double sag = Math.Min(30, w * .018);
            f.Segments.Add(new QuadraticBezierSegment(new Point(w / 2, 12 + 2 * sag), new Point(w + 20, 12), true)); rope.Figures.Add(f);
            var grip = new System.Windows.Shapes.Path { Data = rope, StrokeThickness = 14, Stroke = Brush("#01828F9D"), Cursor = Cursors.SizeNS,
                ToolTip = "按住晾衣绳上下拖动，调整整排高度；松开后记住位置" };
            AttachHeightDrag(grip); Surface.Children.Add(grip);
            var path = new System.Windows.Shapes.Path { Data = rope, StrokeThickness = 1.4, IsHitTestVisible = false,
                Stroke = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0, 130, 143, 157), 0),
                new GradientStop(Color.FromArgb(230, 130, 143, 157), .08), new GradientStop(Color.FromArgb(230, 130, 143, 157), .92),
                new GradientStop(Color.FromArgb(0, 130, 143, 157), 1) }, new Point(0, 0), new Point(1, 0)) };
            Surface.Children.Add(path);
            double spacing = Math.Min(188, (w - 64) / Math.Max(1, count));
            double cardWidth = Math.Min(164, Math.Max(48, spacing - 18));
            for (int i = 0; i < count; i++) {
                double x = w / 2 + (i - (count - 1) / 2.0) * spacing;
                double y = 12 + 4 * sag * (x / w) * (1 - x / w);
                UIElement card = CreatePhoto(store.Items[i], cardWidth, i, animate);
                Canvas.SetLeft(card, x - cardWidth / 2); Canvas.SetTop(card, y - 10); Surface.Children.Add(card);
            }
        }
        void AttachHeightDrag(FrameworkElement grip) {
            bool adjusting = false; double original = 0, initialOffset = 0; int startY = 0;
            Forms.Screen screen = null; double scale = 1;
            Action finish = delegate {
                if (!adjusting) return;
                adjusting = false; Dragging = false;
                try { store.Save(); } catch (Exception ex) { store.Settings.HeightOffset = original; Place(screen); error(ex); }
                KeepUntil = DateTime.Now.AddSeconds(3);
            };
            grip.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) {
                if (Handle == IntPtr.Zero) return;
                Native.POINT pos; Native.GetCursorPos(out pos); startY = pos.Y;
                Native.RECT rect; Native.GetWindowRect(Handle, out rect);
                screen = Forms.Screen.FromHandle(Handle); scale = HwndSource.FromHwnd(Handle).CompositionTarget.TransformToDevice.M22;
                original = store.Settings.HeightOffset; initialOffset = (rect.Top - screen.WorkingArea.Top) / scale;
                adjusting = true; Dragging = true; grip.CaptureMouse(); e.Handled = true;
            };
            grip.PreviewMouseMove += delegate(object s, MouseEventArgs e) {
                if (!adjusting) return;
                if (e.LeftButton != MouseButtonState.Pressed) { finish(); grip.ReleaseMouseCapture(); return; }
                Native.POINT pos; Native.GetCursorPos(out pos);
                store.Settings.HeightOffset = ClampOffset(initialOffset + (pos.Y - startY) / scale, screen.WorkingArea.Height, scale);
                Place(screen); e.Handled = true;
            };
            grip.PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { finish(); grip.ReleaseMouseCapture(); e.Handled = true; };
            grip.LostMouseCapture += delegate { finish(); };
        }
        UIElement CreatePhoto(Photo photo, double width, int index, bool animate) {
            var host = new Canvas { Width = width, Height = 156, ToolTip = "左键复制 · 右键查看 · 拖到另一张上交换\n拖出晾衣绳松手取下并清理 · Esc 取消\nShift + 右键更多操作 · 长按画图 · " + photo.Width + " × " + photo.Height };
            double scale = Math.Min((width - 10) / photo.Width, 112.0 / photo.Height);
            var image = new Image { Source = photo.Thumb, Width = Math.Max(1, photo.Width * scale), Height = Math.Max(1, photo.Height * scale), Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var photoGrid = new Grid(); photoGrid.Children.Add(image);
            var close = Button("×", "取下并删除这张图片", delegate { RemoveAnimated(photo, host); });
            close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top; close.Opacity = 0;
            close.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; RemoveAnimated(photo, host); };
            photoGrid.Children.Add(close);
            var card = Glass(photoGrid, 12); card.Padding = new Thickness(4);
            card.AllowDrop = true;
            Action<DragEventArgs> dragOver = delegate(DragEventArgs e) {
                string file = e.Data.GetData("ShaiTu.PhotoId") as string;
                bool allowed = file != photo.File && store.Items.Any(p => p.File == file);
                e.Effects = allowed ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true;
                card.BorderBrush = allowed ? Brush("#EBA968") : Brush("#F8FFFFFF");
            };
            card.DragEnter += delegate(object s, DragEventArgs e) { dragOver(e); };
            card.DragOver += delegate(object s, DragEventArgs e) { dragOver(e); };
            card.DragLeave += delegate { card.BorderBrush = Brush("#F8FFFFFF"); };
            card.Drop += delegate(object s, DragEventArgs e) {
                string file = e.Data.GetData("ShaiTu.PhotoId") as string;
                Photo source = store.Items.FirstOrDefault(p => p.File == file);
                e.Handled = true; e.Effects = DragDropEffects.None;
                if (source != null && source != photo) Try(delegate { if (store.Swap(source, photo)) e.Effects = DragDropEffects.Link; });
                card.BorderBrush = Brush("#F8FFFFFF");
            };
            card.Measure(new Size(width, 130)); double cardW = card.DesiredSize.Width;
            Canvas.SetTop(card, 16); Canvas.SetLeft(card, (width - cardW) / 2); host.Children.Add(card);
            var pin = new Border { Width = 9, Height = 27, CornerRadius = new CornerRadius(3), BorderBrush = Brush("#EDF1F4"), BorderThickness = new Thickness(.7),
                Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromRgb(148, 157, 168), 0), new GradientStop(Color.FromRgb(234, 238, 242), .36), new GradientStop(Color.FromRgb(160, 168, 178), 1) }, new Point(0, 0), new Point(1, 0)),
                Child = new Border { Height = 1.5, Width = 5, Background = Brush("#80909C"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 9, 0, 0) }, IsHitTestVisible = false };
            Canvas.SetLeft(pin, (width - 9) / 2); host.Children.Add(pin);
            double tilt = index % 3 == 0 ? -2.1 : index % 3 == 1 ? 1.5 : -.7;
            var rotate = new RotateTransform(tilt, width / 2, 10); var move = new TranslateTransform();
            var transforms = new TransformGroup(); transforms.Children.Add(rotate); transforms.Children.Add(move); host.RenderTransform = transforms;
            if (animate && !testMode) { rotate.BeginAnimation(RotateTransform.AngleProperty, Anim(tilt + 9, tilt, .6)); move.BeginAnimation(TranslateTransform.YProperty, Anim(-20, 0, .4)); }
            host.MouseEnter += delegate { close.Opacity = 1; rotate.BeginAnimation(RotateTransform.AngleProperty, Anim(rotate.Angle, tilt + 1, .2)); };
            host.MouseLeave += delegate { close.Opacity = 0; rotate.BeginAnimation(RotateTransform.AngleProperty, Anim(rotate.Angle, tilt, .35)); };
            var menu = new ContextMenu { StaysOpen = false };
            AddMenu(menu, "复制图片", delegate { Copy(photo); }); AddMenu(menu, "预览", delegate { Preview(photo); });
            AddMenu(menu, "用画图编辑", delegate { Edit(photo); }); AddMenu(menu, "另存为…", delegate { SaveAs(photo); });
            AddMenu(menu, "在文件夹中显示", delegate { Try(delegate { Process.Start("explorer.exe", "/select,\"" + photo.File + "\""); }); });
            menu.Items.Add(new Separator()); AddMenu(menu, "取下", delegate { RemoveAnimated(photo, host); });
            menu.Opened += delegate {
                ClosePhotoMenu(); photoMenu = menu; Busy = true;
                Try(delegate { outsideClick = new MenuOutsideClick(menu); });
            };
            menu.Closed += delegate {
                if (photoMenu != menu) return;
                if (outsideClick != null) { outsideClick.Dispose(); outsideClick = null; }
                photoMenu = null; Busy = false; KeepUntil = DateTime.Now.AddMilliseconds(700);
            }; card.ContextMenu = menu;
            card.PreviewMouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
                e.Handled = true; Preview(photo);
            };
            card.ContextMenuOpening += delegate(object s, ContextMenuEventArgs e) {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) e.Handled = true;
            };
            Point down = new Point(); bool pressed = false, held = false, dragged = false;
            var hold = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            hold.Tick += delegate { hold.Stop(); if (pressed && !dragged) { held = true; pressed = false; card.ReleaseMouseCapture(); Edit(photo); } };
            card.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) {
                // Preview events tunnel through the card before reaching its close button.
                DependencyObject origin = e.OriginalSource as DependencyObject;
                while (origin != null && origin != card) {
                    if (origin is Button) return;
                    origin = VisualTreeHelper.GetParent(origin);
                }
                e.Handled = true;
                down = e.GetPosition(card); pressed = true; held = false; dragged = false; hold.Start(); card.CaptureMouse();
            };
            card.LostMouseCapture += delegate { hold.Stop(); pressed = false; };
            card.PreviewMouseMove += delegate(object s, MouseEventArgs e) {
                if (!pressed || held || dragged || e.LeftButton != MouseButtonState.Pressed) return;
                Point now = e.GetPosition(card);
                if (Math.Abs(now.X - down.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - down.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragged = true; hold.Stop(); card.ReleaseMouseCapture(); Dragging = true; host.Opacity = .5;
                bool cancelled = false, released = false;
                QueryContinueDragEventHandler continuation = delegate(object sender, QueryContinueDragEventArgs args) {
                    if (args.EscapePressed || args.Action == DragAction.Cancel) cancelled = true;
                    if (!args.EscapePressed && (args.KeyStates & DragDropKeyStates.LeftMouseButton) == 0) released = true;
                };
                card.QueryContinueDrag += continuation;
                try {
                    Try(delegate {
                        var result = DragDrop.DoDragDrop(card, DragData(photo), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
                        FinishPhotoDrag(photo, result, cancelled, released, Forms.Cursor.Position);
                    });
                } finally {
                    card.QueryContinueDrag -= continuation;
                    Dragging = false; host.Opacity = 1; KeepUntil = DateTime.Now.AddSeconds(1);
                }
            };
            card.PreviewMouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) {
                bool shouldCopy = pressed && !held && !dragged;
                pressed = false; hold.Stop(); card.ReleaseMouseCapture(); if (shouldCopy) Copy(photo); e.Handled = true;
            };
            return host;
        }
        void ClosePhotoMenu() {
            bool wasOpen = photoMenu != null;
            if (photoMenu != null) photoMenu.IsOpen = false;
            if (outsideClick != null) { outsideClick.Dispose(); outsideClick = null; }
            photoMenu = null;
            if (wasOpen) { Busy = false; KeepUntil = DateTime.Now.AddMilliseconds(700); }
        }
        internal void FinishPhotoDrag(Photo photo, DragDropEffects result, bool cancelled, bool released, System.Drawing.Point point) {
            if (cancelled || !released) return;
            if (ActiveRegion().Contains(point) && !(result == DragDropEffects.Move && !File.Exists(photo.File))) return;
            store.Remove(photo);
        }
        static void AddMenu(ContextMenu menu, string label, Action action) { var item = new MenuItem { Header = label }; item.Click += delegate { action(); }; menu.Items.Add(item); }
        public static DataObject DragData(Photo photo) {
            var data = new DataObject(); data.SetFileDropList(new StringCollection { photo.File });
            data.SetData("ShaiTu.PhotoId", photo.File);
            data.SetData(DataFormats.Bitmap, Store.ReadImage(photo.File, false)); return data;
        }
        void Copy(Photo p) {
            Try(delegate {
                var data = DragData(p); data.SetData("ShaiTu.Source", "owned");
                Clipboard.SetDataObject(data, true); OwnClipboardSequence = Native.GetClipboardSequenceNumber();
                Toast("✓ 已复制");
            });
        }
        public void Toast(string message) {
            var text = Text(message, 12, "#16634A"); text.Margin = new Thickness(12, 6, 12, 6);
            var toast = Glass(text, 14); toast.Measure(new Size(400, 50));
            Canvas.SetTop(toast, ToastTop(ActiveBandHeight(Width), toast.DesiredSize.Height, Height));
            Canvas.SetLeft(toast, (Width - toast.DesiredSize.Width) / 2); Surface.Children.Add(toast);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.3) };
            timer.Tick += delegate { timer.Stop(); Surface.Children.Remove(toast); }; timer.Start(); KeepUntil = DateTime.Now.AddSeconds(2);
        }
        public static double ToastTop(double bandHeight, double toastHeight, double windowHeight) {
            return Math.Max(14, Math.Min(bandHeight + 10, windowHeight - toastHeight - 16));
        }
        void RemoveAnimated(Photo p, FrameworkElement element) {
            Busy = true; var fade = Anim(1, 0, .22); var fall = new TranslateTransform();
            var group = element.RenderTransform as TransformGroup; if (group != null) group.Children.Add(fall);
            fall.BeginAnimation(TranslateTransform.YProperty, Anim(0, 50, .22));
            fade.Completed += delegate { Try(delegate { store.Remove(p); }); Busy = false; Rebuild(false); };
            element.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        void Preview(Photo p) {
            if (PreviewRequested != null) { PreviewRequested(p); return; }
            Try(delegate { Process.Start(new ProcessStartInfo(p.File) { UseShellExecute = true }); });
        }
        void Edit(Photo p) { Try(delegate { Process.Start("mspaint.exe", "\"" + p.File + "\""); }); }
        void SaveAs(Photo p) {
            Busy = true;
            try {
                var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "截图-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", AddExtension = true };
                if (dialog.ShowDialog() == true) { File.Copy(p.File, dialog.FileName, true); Toast("✓ 已保存"); }
            } catch (Exception ex) { error(ex); } finally { Busy = false; KeepUntil = DateTime.Now.AddSeconds(1); }
        }
        void Try(Action action) { try { action(); } catch (Exception e) { error(e); } }
    }
}
