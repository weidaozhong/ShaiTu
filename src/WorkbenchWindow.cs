using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ShaiTu {
    // A text-extraction-only window. It is resizable, closable by clicking outside, and
    // shows no word boxes until the user selects something.
    public sealed class WorkbenchWindow : Window {
        readonly Store store;
        readonly Photo photo;
        readonly Action<Exception> error;
        readonly Action closed;
        BitmapSource image;
        OcrDocument document;
        string editedText;
        bool recognizing, closing, suppressDeactivate, dialogOpen, everActivated;
        double scale = 1, dpiScale = 1;
        IntPtr handle;
        readonly List<OcrWord> selected = new List<OcrWord>();
        bool dragging;
        Point dragStart;
        bool resizing;
        ResizeEdge resizeEdge;
        Point resizeStart;
        double resizeLeft, resizeTop, resizeWidth, resizeHeight;

        ScrollViewer scroller;
        Canvas host, overlay, selectionLayer, dragLayer;
        Image imageView;
        TextBlock status, title, dimensions;
        Button copySelected, copyAll, editText, retry;
        Border toast;
        DispatcherTimer toastTimer;

        public WorkbenchWindow(Store state, Photo item, Action<Exception> onError, Action onClosed) {
            store = state; photo = item; error = onError; closed = onClosed;
            Title = "晒图 · 取字";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = true;
            Topmost = true;
            MinWidth = 640; MinHeight = 360;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Build();
            if (image != null) {
                // Size to the image, not to a fixed share of the screen.
                double maxWidth = Math.Max(MinWidth, SystemParameters.WorkArea.Width * .78);
                double maxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height * .78);
                Width = Math.Max(MinWidth, Math.Min(maxWidth, image.PixelWidth + 64));
                Height = Math.Max(MinHeight, Math.Min(maxHeight, image.PixelHeight + 150));
            } else {
                Width = 900; Height = 620;
            }
            SourceInitialized += delegate {
                handle = new WindowInteropHelper(this).Handle;
                HwndSource source = HwndSource.FromHwnd(handle);
                if (source != null) dpiScale = source.CompositionTarget.TransformToDevice.M22;
            };
            Loaded += delegate { Fit(); StartOcr(); };
            SizeChanged += delegate { Fit(); };
            Activated += delegate { everActivated = true; suppressDeactivate = false; };
            StateChanged += delegate {
                bool minimized = WindowState == WindowState.Minimized;
                suppressDeactivate = minimized;
                if (MinimizedChanged != null) MinimizedChanged(minimized);
            };
            Deactivated += delegate {
                if (!everActivated || closing || suppressDeactivate || dialogOpen || WindowState == WindowState.Minimized) return;
                // Clicking outside has the same effect as the minimize button. Defer it so
                // WPF's activation callback is not re-entered.
                Dispatcher.BeginInvoke(new Action(delegate {
                    if (!closing && !suppressDeactivate && !dialogOpen && WindowState != WindowState.Minimized) {
                        suppressDeactivate = true;
                        WindowState = WindowState.Minimized;
                    }
                }), DispatcherPriority.Background);
            };
            Closed += delegate { closing = true; if (closed != null) closed(); };
            PreviewKeyDown += OnKeyDown;
        }

        public event Action<bool> MinimizedChanged;

        public void Restore() {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
        }

        public bool HasText { get { return document != null && document.WordCount > 0; } }
        public int WordCount { get { return document == null ? 0 : document.WordCount; } }
        public string RecognizedText { get { return document == null ? "" : document.Text; } }
        public double DisplayScale { get { return scale; } }

        void Build() {
            var root = new Border {
                Background = Brush("#F6F8FB"), BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16), Margin = new Thickness(18),
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .22, BlurRadius = 28, ShadowDepth = 8 }
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var shell = new Grid();
            shell.Children.Add(grid);
            root.Child = shell;
            AddResizeZones(shell);
            var grip = new System.Windows.Shapes.Path {
                Data = Geometry.Parse("M 0,9 L 9,0 M 4,13 L 13,4 M 8,17 L 17,8"),
                Stroke = Brush("#94A3B8"), StrokeThickness = 1, Opacity = .4, IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 6)
            };
            Panel.SetZIndex(grip, 25);
            shell.Children.Add(grip);
            Content = root;

            grid.Children.Add(BuildTopBar());
            var content = new Grid { Margin = new Thickness(14, 0, 14, 14) };
            scroller = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brush("#E9EDF2") };
            host = new Canvas { Background = Brushes.Transparent };
            imageView = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(imageView, BitmapScalingMode.HighQuality);
            host.Children.Add(imageView);
            overlay = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = true };
            selectionLayer = new Canvas { IsHitTestVisible = false };
            dragLayer = new Canvas { IsHitTestVisible = false };
            overlay.Children.Add(selectionLayer); overlay.Children.Add(dragLayer);
            host.Children.Add(overlay);
            overlay.MouseLeftButtonDown += OnOverlayDown;
            overlay.MouseMove += OnOverlayMove;
            overlay.MouseLeftButtonUp += OnOverlayUp;
            scroller.Content = host;
            var contentBorder = new Border { CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = scroller, BorderBrush = Brush("#DCE3EA"), BorderThickness = new Thickness(1) };
            content.Children.Add(contentBorder);
            Grid.SetRow(content, 1); grid.Children.Add(content);

            var bottom = BuildBottomBar();
            Grid.SetRow(bottom, 2); grid.Children.Add(bottom);

            toast = new Border {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 76),
                Background = Brush("#F2FFFFFF"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 8, 14, 8), Visibility = Visibility.Collapsed,
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .18, BlurRadius = 12, ShadowDepth = 3 }, IsHitTestVisible = false
            };
            Grid.SetRowSpan(toast, 3); grid.Children.Add(toast);

            try { image = Store.ReadImage(photo.File, false); } catch (Exception ex) { error(ex); }
            if (image != null) {
                imageView.Source = image;
                title.Text = "截图 " + (File.Exists(photo.File) ? new FileInfo(photo.File).LastWriteTime.ToString("MM-dd HH:mm") : "");
                dimensions.Text = image.PixelWidth + " × " + image.PixelHeight + " · " + (File.Exists(photo.File) ? new FileInfo(photo.File).Length / 1024 + " KB" : "");
            }
        }

        FrameworkElement BuildTopBar() {
            var bar = new Border { Background = Brushes.Transparent, Padding = new Thickness(18, 0, 12, 0) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush("#1F2937"), VerticalAlignment = VerticalAlignment.Center };
            dimensions = new TextBlock { FontSize = 12, Foreground = Brush("#64748B"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(title); left.Children.Add(dimensions);
            left.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } } };
            grid.Children.Add(left);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var minimize = FlatButton("–", delegate { suppressDeactivate = true; WindowState = WindowState.Minimized; });
            minimize.FontSize = 18; minimize.Padding = new Thickness(11, 0, 11, 4); minimize.ToolTip = "最小化"; actions.Children.Add(minimize);
            var close = FlatButton("×", delegate { Close(); });
            close.FontSize = 20; close.Padding = new Thickness(12, 0, 12, 2); close.ToolTip = "关闭 (Esc)"; actions.Children.Add(close);
            Grid.SetColumn(actions, 1); grid.Children.Add(actions);
            bar.Child = grid;
            return bar;
        }

        Border BuildBottomBar() {
            var bar = new Border { Background = Brush("#FBFDFF"), BorderBrush = Brush("#E2E8F0"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(18, 10, 18, 12), CornerRadius = new CornerRadius(0, 0, 16, 16) };
            var panel = new DockPanel();
            status = new TextBlock { Text = "正在识别文字…", FontSize = 12, Foreground = Brush("#475569"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(status, Dock.Left); panel.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            retry = FlatButton("重新识别", delegate { document = null; editedText = null; selected.Clear(); RedrawSelection(); StartOcr(); });
            retry.FontSize = 12; retry.IsEnabled = false; buttons.Children.Add(retry);
            editText = FlatButton("编辑全文", delegate { EditFullText(); });
            editText.FontSize = 12; editText.IsEnabled = false; buttons.Children.Add(editText);
            var selectAll = FlatButton("全选", delegate { SelectAll(); });
            selectAll.FontSize = 12; buttons.Children.Add(selectAll);
            copyAll = FlatButton("复制全部", delegate { Copy(true); });
            copyAll.FontSize = 12; copyAll.IsEnabled = false; buttons.Children.Add(copyAll);
            copySelected = FlatButton("复制选中", delegate { Copy(false); });
            copySelected.FontSize = 12; copySelected.IsEnabled = false; buttons.Children.Add(copySelected);
            DockPanel.SetDock(buttons, Dock.Right); panel.Children.Add(buttons);
            bar.Child = panel;
            return bar;
        }

        static Button FlatButton(string label, Action action) {
            var button = new Button {
                Content = label, FontSize = 12, Padding = new Thickness(10, 6, 10, 6), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Foreground = Brush("#334155"), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            factory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(4, 2, 4, 2));
            factory.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = factory };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#E8EFF7")));
            template.Triggers.Add(hover);
            button.Template = template;
            button.Click += delegate { action(); };
            return button;
        }

        static Brush Brush(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }

        void Fit() {
            if (image == null || scroller == null) return;
            double availableWidth = scroller.ActualWidth > 20 ? scroller.ActualWidth - 2 : ActualWidth - 60;
            double availableHeight = scroller.ActualHeight > 20 ? scroller.ActualHeight - 2 : ActualHeight - 160;
            if (availableWidth < 20 || availableHeight < 20) return;
            scale = Math.Min(availableWidth / image.PixelWidth, availableHeight / image.PixelHeight);
            if (scale > 1) scale = 1;
            ApplyScale();
        }

        void ApplyScale() {
            if (image == null) return;
            host.Width = Math.Max(1, image.PixelWidth * scale);
            host.Height = Math.Max(1, image.PixelHeight * scale);
            imageView.Width = host.Width; imageView.Height = host.Height;
            overlay.Width = host.Width; overlay.Height = host.Height;
            selectionLayer.Width = host.Width; selectionLayer.Height = host.Height;
            dragLayer.Width = host.Width; dragLayer.Height = host.Height;
            RedrawSelection();
        }

        public void StartOcr() {
            if (recognizing || image == null || closing) return;
            recognizing = true; retry.IsEnabled = false; copyAll.IsEnabled = false; copySelected.IsEnabled = false; editText.IsEnabled = false;
            status.Text = "正在识别文字…";
            BitmapSource source = image;
            ThreadPool.QueueUserWorkItem(delegate {
                try {
                    OcrDocument result = OcrService.Recognize(source);
                    Dispatcher.BeginInvoke(new Action(delegate { if (!closing) OnRecognized(result); }));
                } catch (Exception ex) {
                    Dispatcher.BeginInvoke(new Action(delegate { if (!closing) OnRecognizeFailed(ex); }));
                }
            });
        }

        void OnRecognized(OcrDocument result) {
            recognizing = false;
            document = result; editedText = null; selected.Clear();
            retry.IsEnabled = true;
            bool hasText = result != null && result.WordCount > 0;
            copyAll.IsEnabled = hasText; editText.IsEnabled = hasText; copySelected.IsEnabled = false;
            RedrawSelection();
            status.Text = hasText ? "共 " + result.WordCount + " 个词、 " + result.Lines.Count + " 行；拖选文字后复制" : "没有识别到文字，换一张图或重试";
        }

        void OnRecognizeFailed(Exception ex) {
            recognizing = false; retry.IsEnabled = true;
            status.Text = ex.Message;
            error(ex);
        }

        void UpdateStatus() {
            if (document == null) return;
            if (selected.Count == 0) {
                status.Text = "共 " + document.WordCount + " 个词、 " + document.Lines.Count + " 行；拖选文字后复制";
                copySelected.IsEnabled = false;
            } else {
                string text = OcrSelection.Text(document, selected);
                status.Text = "已选 " + text.Length + " 字（" + selected.Count + " 个词）";
                copySelected.IsEnabled = true;
            }
        }

        public void SelectAll() {
            if (document == null) return;
            selected.Clear();
            selected.AddRange(OcrSelection.All(document));
            RedrawSelection();
            UpdateStatus();
        }

        void RedrawSelection() {
            if (selectionLayer == null) return;
            selectionLayer.Children.Clear();
            foreach (OcrWord word in selected) {
                var rect = new Rectangle { Width = Math.Max(1, word.Box.Width * scale), Height = Math.Max(1, word.Box.Height * scale), Fill = Brush("#552F80ED"), RadiusX = 2, RadiusY = 2 };
                Canvas.SetLeft(rect, word.Box.X * scale); Canvas.SetTop(rect, word.Box.Y * scale);
                selectionLayer.Children.Add(rect);
            }
        }

        Rect ImageArea(Point a, Point b) {
            double x1 = Math.Min(a.X, b.X) / scale, y1 = Math.Min(a.Y, b.Y) / scale;
            double x2 = Math.Max(a.X, b.X) / scale, y2 = Math.Max(a.Y, b.Y) / scale;
            return new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
        }

        void OnOverlayDown(object sender, MouseButtonEventArgs e) {
            if (document == null) return;
            Point point = e.GetPosition(overlay);
            if (e.ClickCount == 2) {
                OcrWord word = OcrSelection.WordAt(document, new Point(point.X / scale, point.Y / scale));
                selected.Clear(); if (word != null) selected.Add(word);
                RedrawSelection(); UpdateStatus(); e.Handled = true; return;
            }
            if (e.ClickCount == 3) {
                OcrLine line = OcrSelection.LineAt(document, new Point(point.X / scale, point.Y / scale));
                selected.Clear(); if (line != null) selected.AddRange(line.Words);
                RedrawSelection(); UpdateStatus(); e.Handled = true; return;
            }
            dragging = true; dragStart = point; selected.Clear(); RedrawSelection(); UpdateStatus();
            overlay.CaptureMouse(); e.Handled = true;
        }

        void OnOverlayMove(object sender, MouseEventArgs e) {
            if (!dragging) return;
            Point point = e.GetPosition(overlay);
            Rect area = ImageArea(dragStart, point);
            dragLayer.Children.Clear();
            var rect = new Rectangle {
                Width = Math.Max(1, area.Width * scale), Height = Math.Max(1, area.Height * scale),
                Stroke = Brush("#2F80ED"), StrokeThickness = 1.4, Fill = Brush("#142F80ED"),
                StrokeDashArray = new DoubleCollection { 3, 2 }
            };
            Canvas.SetLeft(rect, area.X * scale); Canvas.SetTop(rect, area.Y * scale);
            dragLayer.Children.Add(rect);
            selected.Clear(); selected.AddRange(OcrSelection.Words(document, area));
            RedrawSelection(); UpdateStatus();
        }

        void OnOverlayUp(object sender, MouseButtonEventArgs e) {
            if (!dragging) return;
            dragging = false; overlay.ReleaseMouseCapture();
            dragLayer.Children.Clear();
            UpdateStatus();
            e.Handled = true;
        }

        void OnKeyDown(object sender, KeyEventArgs e) {
            if (Keyboard.FocusedElement is TextBox) return;
            if (e.Key == Key.Escape) {
                if (selected.Count > 0) { selected.Clear(); RedrawSelection(); UpdateStatus(); }
                else Close();
                e.Handled = true; return;
            }
            if (Keyboard.Modifiers == ModifierKeys.Control) {
                if (e.Key == Key.A) { SelectAll(); e.Handled = true; return; }
                if (e.Key == Key.C) { Copy(false); e.Handled = true; return; }
                if (e.Key == Key.E) { EditFullText(); e.Handled = true; return; }
            }
        }

        void Copy(bool all) {
            if (document == null) return;
            if (all) SelectAll();
            string text;
            if (all || selected.Count == 0) text = String.IsNullOrEmpty(editedText) ? document.Text : editedText;
            else text = OcrSelection.Text(document, selected);
            if (String.IsNullOrEmpty(text)) { status.Text = "没有可复制的文字"; return; }
            try {
                Clipboard.SetText(text);
                ShowToast("✓ 已复制 " + text.Length + " 字");
            } catch (Exception ex) { error(ex); }
        }

        void EditFullText() {
            if (document == null) return;
            suppressDeactivate = true; dialogOpen = true;
            try {
                var dialog = new Window {
                    Title = "编辑识别文字", Owner = this, Width = 640, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false, FontFamily = FontFamily, Background = Brush("#F6F8FB")
                };
                var grid = new Grid { Margin = new Thickness(16) };
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var box = new TextBox { Text = String.IsNullOrEmpty(editedText) ? document.Text : editedText, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14, Padding = new Thickness(10) };
                Grid.SetRow(box, 0); grid.Children.Add(box);
                var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var cancel = new Button { Content = "取消", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
                cancel.Click += delegate { dialog.DialogResult = false; };
                var accept = new Button { Content = "使用修改后的文字", Padding = new Thickness(14, 6, 14, 6) };
                accept.Click += delegate { dialog.DialogResult = true; };
                bar.Children.Add(cancel); bar.Children.Add(accept);
                Grid.SetRow(bar, 1); grid.Children.Add(bar);
                dialog.Content = grid;
                if (dialog.ShowDialog() == true) {
                    editedText = box.Text;
                    status.Text = "已使用修改后的全文；复制时优先使用它";
                }
            } finally {
                dialogOpen = false; suppressDeactivate = false;
            }
        }

        void ShowToast(string message) {
            toast.Child = new TextBlock { Text = message, FontSize = 12, Foreground = Brush("#16634A") };
            toast.Visibility = Visibility.Visible;
            if (toastTimer != null) toastTimer.Stop();
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            toastTimer.Tick += delegate { toastTimer.Stop(); toast.Visibility = Visibility.Collapsed; };
            toastTimer.Start();
        }

        // Custom edge zones resize the window directly. WM_NCHITTEST on a layered
        // (AllowsTransparency) window is unreliable, and this works at any DPI.
        void AddResizeZones(Grid shell) {
            AddZone(shell, 6, Double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch, Cursors.SizeWE, ResizeEdge.Left, 0);
            AddZone(shell, 6, Double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch, Cursors.SizeWE, ResizeEdge.Right, 0);
            AddZone(shell, Double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Top, Cursors.SizeNS, ResizeEdge.Top, 0);
            AddZone(shell, Double.NaN, 6, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, Cursors.SizeNS, ResizeEdge.Bottom, 0);
            AddZone(shell, 12, 12, HorizontalAlignment.Left, VerticalAlignment.Top, Cursors.SizeNWSE, ResizeEdge.Left | ResizeEdge.Top, 1);
            AddZone(shell, 12, 12, HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.SizeNESW, ResizeEdge.Right | ResizeEdge.Top, 1);
            AddZone(shell, 12, 12, HorizontalAlignment.Left, VerticalAlignment.Bottom, Cursors.SizeNESW, ResizeEdge.Left | ResizeEdge.Bottom, 1);
            AddZone(shell, 12, 12, HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE, ResizeEdge.Right | ResizeEdge.Bottom, 1);
        }

        void AddZone(Grid shell, double width, double height, HorizontalAlignment horizontal, VerticalAlignment vertical, Cursor cursor, ResizeEdge edge, int layer) {
            var zone = new Border { Background = Brushes.Transparent, HorizontalAlignment = horizontal, VerticalAlignment = vertical, Cursor = cursor };
            if (!Double.IsNaN(width)) zone.Width = width;
            if (!Double.IsNaN(height)) zone.Height = height;
            Panel.SetZIndex(zone, 30 + layer);
            zone.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { BeginResize(edge, sender, e); };
            zone.MouseMove += delegate(object sender, MouseEventArgs e) { ContinueResize(e); };
            zone.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e) { EndResize(sender, e); };
            shell.Children.Add(zone);
        }

        void BeginResize(ResizeEdge edge, object sender, MouseButtonEventArgs e) {
            if (WindowState == WindowState.Minimized) return;
            resizing = true; resizeEdge = edge; resizeStart = ScreenPoint(e);
            resizeLeft = Left; resizeTop = Top; resizeWidth = ActualWidth; resizeHeight = ActualHeight;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        void ContinueResize(MouseEventArgs e) {
            if (!resizing) return;
            if (e.LeftButton != MouseButtonState.Pressed) { resizing = false; return; }
            Point current = ScreenPoint(e);
            double dx = current.X - resizeStart.X, dy = current.Y - resizeStart.Y;
            double left = resizeLeft, top = resizeTop, width = resizeWidth, height = resizeHeight;
            if ((resizeEdge & ResizeEdge.Left) != 0) {
                double candidate = resizeWidth - dx;
                if (candidate < MinWidth) { width = MinWidth; left = resizeLeft + resizeWidth - MinWidth; }
                else { width = candidate; left = resizeLeft + dx; }
            } else if ((resizeEdge & ResizeEdge.Right) != 0) {
                width = Math.Max(MinWidth, resizeWidth + dx);
            }
            if ((resizeEdge & ResizeEdge.Top) != 0) {
                double candidate = resizeHeight - dy;
                if (candidate < MinHeight) { height = MinHeight; top = resizeTop + resizeHeight - MinHeight; }
                else { height = candidate; top = resizeTop + dy; }
            } else if ((resizeEdge & ResizeEdge.Bottom) != 0) {
                height = Math.Max(MinHeight, resizeHeight + dy);
            }
            Left = left; Top = top; Width = width; Height = height;
            e.Handled = true;
        }

        void EndResize(object sender, MouseButtonEventArgs e) {
            if (!resizing) return;
            resizing = false;
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        Point ScreenPoint(MouseEventArgs e) {
            Point physical = PointToScreen(e.GetPosition(this));
            double factor = dpiScale > 0 ? dpiScale : 1;
            return new Point(physical.X / factor, physical.Y / factor);
        }

        [Flags] enum ResizeEdge { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
    }
}
