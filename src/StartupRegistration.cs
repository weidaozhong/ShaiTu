using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic;
using Microsoft.Win32;

namespace ShaiTu {
    public sealed class StartupRegistration {
        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string ApprovalKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\StartupFolder";
        public const string Arguments = "--startup";
        public const int DelayMilliseconds = 15000;
        readonly string executable;
        readonly bool isolated;
        public readonly string Shortcut;
        public StartupRegistration(string path, string startupFolder = null) {
            executable = Path.GetFullPath(path);
            isolated = startupFolder != null;
            Shortcut = Path.Combine(startupFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ShaiTu.lnk");
        }
        static object Call(object target, string method, params object[] args) { return Interaction.CallByName(target, method, CallType.Method, args); }
        static object Property(object target, string property) { return Interaction.CallByName(target, property, CallType.Get); }
        static void Set(object target, string property, object value) {
            try { Interaction.CallByName(target, property, CallType.Let, value); }
            catch (Exception ex) { throw new IOException("无法设置启动快捷方式属性：" + property, ex); }
        }
        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        public static bool ApprovalAllows(byte[] status) { return status == null || status.Length == 0 || status[0] == 2 || status[0] == 6; }
        public bool Exists { get { return File.Exists(Shortcut); } }
        public bool Enabled {
            get {
                if (!Exists) return false;
                if (isolated) return true;
                using (var key = Registry.CurrentUser.OpenSubKey(ApprovalKey)) return ApprovalAllows(key == null ? null : key.GetValue("ShaiTu.lnk") as byte[]);
            }
        }
        public bool MatchesPath {
            get {
                if (!Exists) return false;
                object shell = null, link = null;
                try {
                    shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)); link = Call(shell, "CreateShortcut", Shortcut);
                    return String.Equals((string)Property(link, "TargetPath"), executable, StringComparison.OrdinalIgnoreCase)
                        && (string)Property(link, "Arguments") == Arguments
                        && String.Equals((string)Property(link, "WorkingDirectory"), Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase);
                } finally { Release(link); Release(shell); }
            }
        }
        public void Enable() {
            Directory.CreateDirectory(Path.GetDirectoryName(Shortcut));
            object shell = null, link = null;
            try {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)); link = Call(shell, "CreateShortcut", Shortcut);
                Set(link, "TargetPath", executable); Set(link, "Arguments", Arguments);
                Set(link, "WorkingDirectory", Path.GetDirectoryName(executable)); Set(link, "IconLocation", executable + ",0");
                Set(link, "Description", "晒图：登录后延迟 15 秒启动"); Call(link, "Save");
            } finally { Release(link); Release(shell); }
            if (!MatchesPath) throw new IOException("未能保存开机启动快捷方式。 ");
            if (!isolated) using (var key = Registry.CurrentUser.CreateSubKey(ApprovalKey)) {
                var enabled = new byte[12]; enabled[0] = 2; key.SetValue("ShaiTu.lnk", enabled, RegistryValueKind.Binary);
            }
        }
        public void Disable() {
            if (Exists) File.Delete(Shortcut);
            if (!isolated) using (var key = Registry.CurrentUser.OpenSubKey(ApprovalKey, true)) if (key != null) key.DeleteValue("ShaiTu.lnk", false);
        }
        public static bool LegacyEnabled() {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey)) if (run == null || run.GetValue("ShaiTu") == null) return false;
            using (var approval = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run"))
                return ApprovalAllows(approval == null ? null : approval.GetValue("ShaiTu") as byte[]);
        }
        public static void RemoveLegacy() {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (run != null) run.DeleteValue("ShaiTu", false);
        }
        public void Migrate() {
            if (isolated) return;
            if (Exists) { if (Enabled && !MatchesPath) Enable(); RemoveLegacy(); }
            else if (LegacyEnabled()) { Enable(); RemoveLegacy(); }
        }
    }
}
