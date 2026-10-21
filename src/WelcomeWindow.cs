using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ShaiTu {
    // First-run guide. A portable extraction has no photos yet, so without this the app
    // would only show a tray icon and look like it did nothing.
    public sealed class WelcomeWindow : Window {
        public WelcomeWindow(Action capture, Action import) {
            Title = "晒图 · 开始使用";
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = true; ResizeMode = ResizeMode.NoResize;
            Width = 560; Height = 470; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            var root = new Border {
                Background = LineWindow.Brush("#F8FAFC"), BorderBrush = LineWindow.Brush("#D8E0EA"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18), Margin = new Thickness(18),
                Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .2, BlurRadius = 28, ShadowDepth = 8 }
            };
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(72) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
            root.Child = grid; Content = root;

            var header = new Grid { Margin = new Thickness(24, 10, 14, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(new TextBlock { Text = "晒图已就绪", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = LineWindow.Brush("#1F2937") });
            heading.Children.Add(new TextBlock { Text = "截图晾衣绳 + 本地取字", FontSize = 12, Foreground = LineWindow.Brush("#64748B"), Margin = new Thickness(0, 4, 0, 0) });
            header.Children.Add(heading);
            var close = new Button { Content = "×", FontSize = 22, Foreground = LineWindow.Brush("#64748B"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Padding = new Thickness(10, 0, 10, 4) };
            close.Click += delegate { Close(); };
            Grid.SetColumn(close, 1); header.Children.Add(close);
            Grid.SetRow(header, 0); grid.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(26, 4, 26, 0) };
            body.Children.Add(Line("晒图已在系统托盘运行。截图、取字和保存都不需要额外配置。", true));
            body.Children.Add(Line("截图：使用 Snipaste 快捷键截图，完成后选择“钉在桌面”；没有 Snipaste 时，用托盘 → Windows 截图。"));
            body.Children.Add(Line("取字：右键任意图片，或托盘 → 截图并取字（识别后复制）。"));
            body.Children.Add(Line("展开 / 收起：Ctrl + Alt + T，或屏幕右侧小把手。"));
            body.Children.Add(Line("取字使用本机 Windows 组件，不下载模型、不上传图片，也不需要填写路径。"));
            Grid.SetRow(body, 1); grid.Children.Add(body);

            var footer = new Grid { Margin = new Thickness(24, 0, 24, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(new TextBlock { Text = "以后可在托盘菜单查看使用说明", FontSize = 11, Foreground = LineWindow.Brush("#94A3B8"), VerticalAlignment = VerticalAlignment.Center });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Button("开始使用", false, delegate { Close(); }));
            actions.Children.Add(Button("添加图片…", false, delegate { Close(); if (import != null) import(); }));
            actions.Children.Add(Button("截图", true, delegate { Close(); if (capture != null) capture(); }));
            Grid.SetColumn(actions, 1); footer.Children.Add(actions);
            Grid.SetRow(footer, 2); grid.Children.Add(footer);

            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        }

        static TextBlock Line(string text, bool first = false) {
            return new TextBlock {
                Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 22,
                Foreground = LineWindow.Brush(first ? "#334155" : "#475569"),
                Margin = new Thickness(0, first ? 0 : 10, 0, 0)
            };
        }

        static Button Button(string label, bool primary, Action action) {
            var button = new Button {
                Content = label, FontSize = 12, Padding = new Thickness(15, 7, 15, 7), Margin = new Thickness(8, 0, 0, 0),
                Foreground = primary ? Brushes.White : LineWindow.Brush("#334155"),
                Background = primary ? LineWindow.Brush("#2F80ED") : LineWindow.Brush("#E8EEF6"),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand
            };
            button.Click += delegate { action(); };
            return button;
        }
    }
}
