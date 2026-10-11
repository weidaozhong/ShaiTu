using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace ShaiTu {
    public static class HotkeyBinding {
        public static bool IsValid(uint modifiers, uint key) {
            return (modifiers & ~15u) == 0 && key >= 8 && key <= 254 && key != 16 && key != 17 && key != 18 && key != 91 && key != 92
                && key != 160 && key != 161 && key != 162 && key != 163 && key != 164 && key != 165;
        }
        public static string Label(uint modifiers, uint key) {
            var names = new List<string>();
            if ((modifiers & 2) != 0) names.Add("Ctrl");
            if ((modifiers & 1) != 0) names.Add("Alt");
            if ((modifiers & 4) != 0) names.Add("Shift");
            if ((modifiers & 8) != 0) names.Add("Win");
            names.Add(key == 175 ? "音量+" : key == 174 ? "音量−" : ((Forms.Keys)key).ToString());
            return String.Join(" + ", names);
        }
    }
    public sealed class DisplaySession {
        public bool ManualHeld { get; private set; }
        public bool CapturePending { get; private set; }
        public DateTime ConfirmUntil { get; private set; }
        public void ExpandManually() { ManualHeld = true; }
        public void CollapseManually() { ManualHeld = false; CapturePending = false; ConfirmUntil = DateTime.MinValue; }
        public bool AcceptCapture(DateTime now, bool fullscreen) {
            CapturePending = fullscreen;
            ConfirmUntil = fullscreen ? DateTime.MinValue : now.AddSeconds(5);
            return !fullscreen;
        }
        public bool ResumeCapture(DateTime now) {
            if (!CapturePending) return false;
            CapturePending = false; ConfirmUntil = now.AddSeconds(5); return true;
        }
        public bool HoldVisible(DateTime now) { return ManualHeld || now < ConfirmUntil; }
    }
    public sealed class HotkeyDialog : Window {
        public readonly TextBox KeyBox = new TextBox { IsReadOnly = true, FontSize = 18, Padding = new Thickness(12), Margin = new Thickness(0, 14, 0, 10) };
        public uint Modifiers { get; private set; }
        public uint VirtualKey { get; private set; }
        public HotkeyDialog(uint modifiers, uint key, Func<uint, uint, string> save) {
            Title = "展开 / 收起快捷键"; Icon = AppIcons.WindowIcon; Width = 420; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI");
            var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
            panel.Children.Add(new TextBlock { Text = "点击按键框，按下你想使用的快捷键。", FontSize = 14 });
            panel.Children.Add(new TextBlock { Text = "支持单键或组合键；再次按下会收起。\n只设置晒图的展开 / 收起，不更改 Snipaste 截图键。", Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(KeyBox);
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = LineWindow.Brush("#B45309"), Margin = new Thickness(0, 0, 0, 12) }; panel.Children.Add(status);
            Action<uint, uint> set = delegate(uint m, uint k) { Modifiers = m; VirtualKey = k; KeyBox.Text = HotkeyBinding.Label(m, k); status.Text = ""; };
            set(modifiers, key);
            KeyBox.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                Key pressed = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(pressed), mods = (uint)Keyboard.Modifiers;
                e.Handled = true;
                if (HotkeyBinding.IsValid(mods, vk)) set(mods, vk);
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
            var reset = new Button { Content = "恢复默认", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
            reset.Click += delegate { set(3, 84); KeyBox.Focus(); }; buttons.Children.Add(reset);
            var cancel = new Button { Content = "取消", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
            cancel.Click += delegate { DialogResult = false; }; buttons.Children.Add(cancel);
            var accept = new Button { Content = "保存", Padding = new Thickness(10, 6, 10, 6) };
            accept.Click += delegate { string failure = save(Modifiers, VirtualKey); if (String.IsNullOrEmpty(failure)) DialogResult = true; else { status.Text = failure; KeyBox.Focus(); } }; buttons.Children.Add(accept);
            Loaded += delegate { KeyBox.Focus(); };
        }
    }
}
