using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Forms = System.Windows.Forms;

namespace ShaiTu {
    public static class SnipasteCommands {
        public static string RunningExecutable() {
            foreach (var process in Process.GetProcessesByName("Snipaste")) using (process) {
                try { return process.MainModule.FileName; } catch { }
            }
            return null;
        }
        public static void Execute(string command) {
            if (command != "snip") throw new ArgumentException("Unsupported Snipaste command.");
            string executable = RunningExecutable();
            if (executable == null) throw new InvalidOperationException("请先启动 Snipaste，然后使用它原来的截图快捷键。");
            using (var process = Process.Start(new ProcessStartInfo(executable, command) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable)
            })) { }
        }
        public static string HotkeyLabel() {
            string executable = RunningExecutable();
            if (executable == null) return "使用 Snipaste 原快捷键";
            try {
                string section = "";
                foreach (string raw in File.ReadAllLines(Path.Combine(Path.GetDirectoryName(executable), "config.ini"))) {
                    string text = raw.Trim();
                    if (text.StartsWith("[") && text.EndsWith("]")) { section = text; continue; }
                    if (section != "[Hotkey]") continue;
                    int equals = text.IndexOf('=');
                    if (equals > 0 && text.Substring(0, equals).Trim() == "snip") return ParseHotkey(text.Substring(equals + 1));
                }
            } catch { }
            return "使用 Snipaste 原快捷键";
        }
        public static string ParseHotkey(string value) {
            string[] parts = value.Trim().Trim('"').Split(',');
            long qt; int vk;
            if (parts.Length != 2 || !Int64.TryParse(parts[0].Trim(), out qt) || !Int32.TryParse(parts[1].Trim(), out vk))
                return "使用 Snipaste 原快捷键";
            var names = new List<string>();
            if ((qt & 0x04000000) != 0) names.Add("Ctrl");
            if ((qt & 0x08000000) != 0) names.Add("Alt");
            if ((qt & 0x02000000) != 0) names.Add("Shift");
            if ((qt & 0x10000000) != 0) names.Add("Win");
            string key;
            if (vk == 175) key = "音量+";
            else if (vk == 174) key = "音量−";
            else if (vk == 173) key = "静音";
            else if (vk > 0 && Enum.IsDefined(typeof(Forms.Keys), vk)) key = ((Forms.Keys)vk).ToString();
            else return "使用 Snipaste 原快捷键";
            names.Add(key); return String.Join(" + ", names);
        }
    }
}
