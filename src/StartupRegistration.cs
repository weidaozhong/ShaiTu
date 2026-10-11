using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
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
        // Use the Unicode Shell interface directly: WScript shortcut setters depend on the system locale.
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int capacity, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
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
                object link = null;
                try {
                    link = new ShellLink(); ((IPersistFile)link).Load(Shortcut, 0);
                    var native = (IShellLinkW)link;
                    var path = new StringBuilder(32768); var arguments = new StringBuilder(32768); var directory = new StringBuilder(32768);
                    native.GetPath(path, path.Capacity, IntPtr.Zero, 4);
                    native.GetArguments(arguments, arguments.Capacity); native.GetWorkingDirectory(directory, directory.Capacity);
                    return String.Equals(path.ToString(), executable, StringComparison.OrdinalIgnoreCase)
                        && arguments.ToString() == Arguments
                        && String.Equals(directory.ToString(), Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase);
                } finally { Release(link); }
            }
        }
        public void Enable() {
            Directory.CreateDirectory(Path.GetDirectoryName(Shortcut));
            object link = null;
            try {
                link = new ShellLink(); var native = (IShellLinkW)link;
                native.SetPath(executable); native.SetArguments(Arguments);
                native.SetWorkingDirectory(Path.GetDirectoryName(executable)); native.SetIconLocation(executable, 0);
                native.SetDescription("晒图：登录后延迟 15 秒启动"); ((IPersistFile)link).Save(Shortcut, true);
            } finally { Release(link); }
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
