using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("晒图 · ShaiTu")]
[assembly: System.Reflection.AssemblyVersion("0.5.8.0")]

namespace ShaiTu {
    public sealed class Program {
        Application app;
        Store store;
        LineWindow line;
        EdgeHandleWindow edgeHandle;
        IncomingWatcher watcher;
        SnipasteBridge snipaste;
        readonly PointerDismissal dismissal = new PointerDismissal();
        readonly DisplaySession display = new DisplaySession();
        readonly WindowsCaptureSession windowsCapture = new WindowsCaptureSession();
        Forms.NotifyIcon tray;
        Forms.ContextMenuStrip menu;
        DispatcherTimer timer;
        uint clipboardSequence;
        bool clipboardPending;
        int clipboardAttempts, ticks;
        bool closing;
        bool initializing = true;
        bool fullscreenSuppressed;
        [STAThread] public static int Main(string[] args) {
            bool startupLaunch = args.Length > 0 && args[0] == StartupRegistration.Arguments;
            if (startupLaunch) Thread.Sleep(StartupRegistration.DelayMilliseconds);
            if (args.Length >= 2 && args[0] == "--self-test") return SelfTest.Run(args[1]);
            if (args.Length >= 2 && args[0] == "--snipaste-test") return SelfTest.SnipasteLive(args[1]);
            if (args.Length >= 2 && args[0] == "--layout-metrics") return SelfTest.LayoutMetrics(args[1]);
            if (args.Length >= 2 && args[0] == "--smoke-test") return new Program().Run(Path.GetFullPath(args[1]));
            bool fresh;
            using (var mutex = new Mutex(true, "Local\\ShaiTu.Windows." + Environment.UserName, out fresh)) {
                if (!fresh) { if (!startupLaunch) MessageBox.Show("晒图已在运行，请在任务栏托盘中打开。", "晒图"); return 0; }
                try { return new Program().Run(args.Length >= 2 && args[0] == "--smoke-test" ? Path.GetFullPath(args[1]) : null); }
                catch (Exception ex) { MessageBox.Show("晒图启动失败：" + ex.Message, "晒图"); return 1; }
            }
        }
        int Run(string smokeOutput) {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            store = new Store(smokeOutput == null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaiTu") : Path.Combine(smokeOutput, "native-smoke"));
            if (smokeOutput != null) {
                store.Settings.ClipboardEnabled = false; store.Settings.SnipasteEnabled = false; store.Settings.HeightOffset = 90;
                store.Settings.HotkeyModifiers = 7; store.Settings.HotkeyKey = 120;
            }
            if (smokeOutput == null) {
                try { new StartupRegistration(Forms.Application.ExecutablePath).Migrate(); }
                catch (Exception ex) { store.Log(ex); }
            }
            line = new LineWindow(store, Error, false);
            edgeHandle = new EdgeHandleWindow();
            edgeHandle.OpenRequested += delegate(Forms.Screen screen) {
                if (closing || line.Busy || line.Dragging || store.Items.Count == 0 || Native.IsFullscreen(screen)) return;
                dismissal.Reset(); edgeHandle.Sync(false, screen, line.Handle); line.Reveal(screen, true);
            };
            tray = new Forms.NotifyIcon { Text = "晒图 · 截图随手可取", Icon = AppIcons.CreateTrayIcon(), Visible = true };
            BuildMenu(); tray.DoubleClick += delegate { Show(); };
            watcher = new IncomingWatcher(app.Dispatcher, delegate(string file) { if (store.ImportFile(file, "监听文件夹")) NewPhoto(); }, Error);
            ConfigureWatcher();
            new WindowInteropHelper(line).EnsureHandle();
            line.ClipboardUpdated += delegate { if (store.Settings.ClipboardEnabled || windowsCapture.Active) { clipboardPending = true; clipboardAttempts = 0; } };
            line.ToggleRequested += Toggle;
            snipaste = new SnipasteBridge(delegate(System.Windows.Media.Imaging.BitmapSource image) {
                if (store.ImportImage(image, "Snipaste:" + Path.GetFileName(snipaste.CurrentFile))) NewPhoto();
            }, Error);
            snipaste.ScanRequested += QueueSnipasteScan;
            ConfigureSnipaste(); BuildMenu();
            line.DisplayModeChanged += delegate { display.CollapseManually(); BuildMenu(); if (line.Pinned) line.Reveal(Forms.Screen.FromPoint(Forms.Cursor.Position), true); };
            // Do not ingest a clipboard image that predates this launch.
            clipboardSequence = Native.GetClipboardSequenceNumber();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) }; timer.Tick += Tick; timer.Start();
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { Error(e.Exception); e.Handled = true; };
            app.Exit += delegate { Cleanup(); };
            line.Closed += delegate { if (!closing) { Cleanup(); app.Shutdown(); } };
            line.Place(Forms.Screen.FromPoint(Forms.Cursor.Position));
            // Startup stays hidden; merely restoring old photos never pops up an overlay or toolbar.
            if (!line.HotkeyAvailable && smokeOutput == null) Error(new InvalidOperationException(HotkeyBinding.Label(store.Settings.HotkeyModifiers, store.Settings.HotkeyKey) + " 已被占用，可在托盘重新设置展开快捷键。"));
            bool startupHidden = !line.Revealed && !line.IsVisible;
            initializing = false;
            if (line.Pinned && smokeOutput == null) Show();
            if (smokeOutput != null) {
                line.Place(Forms.Screen.FromPoint(Forms.Cursor.Position));
                string hotkeyFailure;
                bool hotkeyChanges = line.ChangeHotkey(7, 121, out hotkeyFailure) && line.ChangeHotkey(7, 120, out hotkeyFailure);
                var conflictWindow = new LineWindow(store, Error, true); new WindowInteropHelper(conflictWindow).EnsureHandle();
                bool reserved = Native.RegisterHotKey(conflictWindow.Handle, 99, 7 | 0x4000, 118);
                bool conflictHandled = reserved && !line.ChangeHotkey(7, 118, out hotkeyFailure) && store.Settings.HotkeyKey == 120;
                Native.UnregisterHotKey(conflictWindow.Handle, 99); conflictWindow.Close();
                Tick(null, EventArgs.Empty);
                bool emptyEdgeHidden = !edgeHandle.IsVisible && !line.Revealed;
                store.ImportImage(SelfTest.DrawSample(0)); NewPhoto();
                bool captureShown = line.Revealed;
                Show(); bool manualHeld = display.HoldVisible(DateTime.Now.AddHours(1));
                var coveredWindow = new Window { Width = 64, Height = 64, Left = line.Left + 80, Top = line.Top + 30,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true, Icon = AppIcons.WindowIcon };
                coveredWindow.Show();
                Native.RECT coveredRect; Native.GetWindowRect(new WindowInteropHelper(coveredWindow).Handle, out coveredRect);
                var coveredPoint = new System.Drawing.Point((coveredRect.Left + coveredRect.Right) / 2, (coveredRect.Top + coveredRect.Bottom) / 2);
                var coveringDismissal = new PointerDismissal();
                bool coveredStable = line.KeepRegion().Contains(coveredPoint) && !coveringDismissal.Update(DateTime.Now, true, false)
                    && !coveringDismissal.Update(DateTime.Now.AddSeconds(5), true, false);
                coveredWindow.Close();
                line.Tuck(false); edgeHandle.Sync(true, Forms.Screen.FromPoint(Forms.Cursor.Position), line.Handle);
                Native.RECT handleRect; Native.GetWindowRect(edgeHandle.Handle, out handleRect);
                bool edgeShown = edgeHandle.IsVisible && handleRect.Right == Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea.Right;
                edgeHandle.Sync(false, null, line.Handle); Show();
                var smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                smokeTimer.Tick += delegate {
                    smokeTimer.Stop();
                    var screen = Forms.Screen.FromPoint(Forms.Cursor.Position); Native.RECT rect; Native.GetWindowRect(line.Handle, out rect);
                    double scale = HwndSource.FromHwnd(line.Handle).CompositionTarget.TransformToDevice.M22;
                    int targetY = screen.WorkingArea.Top + (int)Math.Round(LineWindow.ClampOffset(90, screen.WorkingArea.Height, scale) * scale);
                    bool geometry = rect.Left == screen.WorkingArea.Left && rect.Top == targetY && rect.Right - rect.Left == screen.WorkingArea.Width;
                    bool ok = line.Handle != IntPtr.Zero && tray.Visible && line.ClipboardListenerAvailable && geometry && startupHidden && hotkeyChanges && conflictHandled && captureShown && manualHeld && coveredStable && edgeShown && emptyEdgeHidden;
                    File.WriteAllLines(Path.Combine(smokeOutput, "native-smoke-results.txt"), new[] {
                        "Native HWND: " + (line.Handle != IntPtr.Zero), "Tray icon: " + tray.Visible,
                        "Clipboard listener registration: " + line.ClipboardListenerAvailable,
                        "Hotkey registration: " + line.HotkeyAvailable, "Physical monitor placement: " + geometry,
                        "Startup hidden, no capsule toolbar: " + startupHidden, "Monitor DPI scale: " + scale,
                        "Two live hotkey changes: " + hotkeyChanges, "New capture reveals window: " + captureShown,
                        "Conflicting hotkey leaves old binding active: " + conflictHandled,
                        "Manual expand survives confirmation timeout: " + manualHeld,
                        "Ordinary HWND beneath expanded panel never causes immediate hiding: " + coveredStable,
                        "Edge handle appears on the physical right working-area boundary: " + edgeShown,
                        "Empty state hides both photos and edge handle: " + emptyEdgeHidden,
                        "No user clipboard content read or written; isolated test data only.", ok ? "PASS: native startup and cleanup" : "FAIL: native startup" });
                    store.ClearForExit(); Cleanup(); app.Shutdown(ok ? 0 : 1);
                }; smokeTimer.Start();
            }
            return app.Run();
        }
        void NewPhoto() {
            var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
            edgeHandle.Sync(false, screen, line.Handle); dismissal.Reset();
            if (display.AcceptCapture(DateTime.Now, Native.IsFullscreen(screen))) {
                line.Reveal(screen, true); line.KeepUntil = display.ConfirmUntil;
            }
        }
        void QueueSnipasteScan() {
            if (closing || app.Dispatcher.HasShutdownStarted) return;
            app.Dispatcher.BeginInvoke(new Action(delegate {
                if (!closing && store.Settings.SnipasteEnabled && !line.Busy && !line.Dragging) snipaste.Tick(true);
            }), DispatcherPriority.Background);
        }
        void Show() { if (store.Items.Count == 0) return; display.ExpandManually(); dismissal.Reset(); edgeHandle.Sync(false, Forms.Screen.FromPoint(Forms.Cursor.Position), line.Handle); line.Reveal(Forms.Screen.FromPoint(Forms.Cursor.Position), true); }
        void Toggle() {
            if (line.Revealed) { display.CollapseManually(); dismissal.Reset(); line.KeepUntil = DateTime.MinValue; line.Tuck(true); }
            else Show();
        }
        void Tick(object sender, EventArgs args) {
            if (closing) return;
            if (windowsCapture.Active) {
                if (windowsCapture.Expire(DateTime.Now) || WindowsSnipCommands.EscapeDown) {
                    windowsCapture.Cancel(); clipboardPending = false; RestoreAfterCapture();
                } else {
                    line.Tuck(false); edgeHandle.Sync(false, null, line.Handle);
                    if (Native.GetClipboardSequenceNumber() != clipboardSequence) clipboardPending = true;
                }
            }
            if (line.Busy || line.Dragging) { dismissal.Reset(); return; }
            watcher.Tick();
            if (store.Settings.SnipasteEnabled) {
                if (ticks % 25 == 0 && String.IsNullOrWhiteSpace(store.Settings.SnipasteFolder)) {
                    string detected = SnipasteBridge.Discover();
                    if (detected != null && !String.Equals(detected, snipaste.HistoryFolder, StringComparison.OrdinalIgnoreCase)) { snipaste.Configure(detected); BuildMenu(); }
                }
                snipaste.Tick();
            }
            if (clipboardPending && (store.Settings.ClipboardEnabled || windowsCapture.Active)) ReadClipboard();
            if (windowsCapture.Active) { dismissal.Reset(); return; }
            if (++ticks % 10 == 0) { try { store.Refresh(); } catch (Exception ex) { Error(ex); } }
            if (store.Items.Count == 0) { line.Tuck(false); edgeHandle.Sync(false, null, line.Handle); dismissal.Reset(); return; }
            var pos = Forms.Cursor.Position; var screen = Forms.Screen.FromPoint(pos);
            if (Native.IsFullscreen(screen)) { fullscreenSuppressed = true; line.Tuck(false); edgeHandle.Sync(false, screen, line.Handle); dismissal.Reset(); return; }
            if (fullscreenSuppressed) {
                fullscreenSuppressed = false;
                if (display.ResumeCapture(DateTime.Now) || line.Pinned || display.ManualHeld) {
                    line.Reveal(screen, true); line.KeepUntil = display.ConfirmUntil;
                }
            }
            if (!line.Revealed) line.Place(screen);
            edgeHandle.Sync(!line.Revealed && store.Settings.EdgeEnabled, screen, line.Handle);
            if (!line.Revealed) { dismissal.Reset(); return; }
            bool inside = line.KeepRegion().Contains(pos) || edgeHandle.Region().Contains(pos);
            if (dismissal.Update(DateTime.Now, inside, line.Pinned || display.HoldVisible(DateTime.Now) || DateTime.Now < line.KeepUntil)) line.Tuck(true);
        }
        void ConfigureSnipaste() {
            snipaste.Configure(store.Settings.SnipasteEnabled ? (String.IsNullOrWhiteSpace(store.Settings.SnipasteFolder) ? SnipasteBridge.Discover() : store.Settings.SnipasteFolder) : null);
        }
        void ReadClipboard() {
            uint sequence = Native.GetClipboardSequenceNumber();
            if (sequence == clipboardSequence || sequence == line.OwnClipboardSequence) { clipboardPending = false; clipboardSequence = sequence; return; }
            try {
                string owner = Native.ClipboardOwnerName();
                if (windowsCapture.TryReceive(DateTime.Now, sequence, line.OwnClipboardSequence, owner,
                    delegate { return Clipboard.ContainsImage() ? Clipboard.GetImage() : null; },
                    delegate(System.Windows.Media.Imaging.BitmapSource image) { store.ImportImage(image, "Windows 截图"); })) {
                    clipboardPending = false; clipboardSequence = sequence; NewPhoto(); return;
                }
                if (windowsCapture.Active || !store.Settings.ClipboardEnabled) { clipboardPending = false; clipboardSequence = sequence; return; }
                // A Snipaste copy action is not a pin request, even when optional clipboard
                // reception is enabled for other applications.
                if (String.Equals(owner, "Snipaste", StringComparison.OrdinalIgnoreCase)) {
                    clipboardPending = false; clipboardSequence = sequence; return;
                }
                if (Clipboard.ContainsData("ShaiTu.Source")) { clipboardPending = false; clipboardSequence = sequence; return; }
                if (Clipboard.ContainsImage()) {
                    var image = Clipboard.GetImage();
                    if (store.ImportImage(image, "剪贴板:" + Native.ClipboardOwnerName())) NewPhoto();
                }
                clipboardPending = false; clipboardSequence = sequence;
            } catch (System.Runtime.InteropServices.COMException ex) {
                if (++clipboardAttempts >= 10) { clipboardPending = false; windowsCapture.Cancel(); RestoreAfterCapture(); Error(ex); }
            } catch (Exception ex) { clipboardPending = false; clipboardSequence = sequence; windowsCapture.Cancel(); RestoreAfterCapture(); Error(ex); }
        }
        void ConfigureWatcher() {
            watcher.Configure(String.IsNullOrWhiteSpace(store.Settings.WatchFolder) ? new string[0] : new[] { store.Settings.WatchFolder });
        }
        void Capture() {
            // Temporarily hide for the snip without losing a manually held session.
            windowsCapture.Cancel(); clipboardPending = false;
            dismissal.Reset(); edgeHandle.Sync(false, null, line.Handle); line.Tuck(false);
            // Let the tray menu close and the hidden overlay repaint before Windows takes the screen.
            var launch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            launch.Tick += delegate {
                launch.Stop(); if (closing) return;
                try {
                    if (SnipasteCommands.RunningExecutable() != null) SnipasteCommands.Execute("snip");
                    else {
                        clipboardSequence = Native.GetClipboardSequenceNumber(); clipboardAttempts = 0;
                        windowsCapture.Start(DateTime.Now, clipboardSequence, WindowsSnipCommands.Execute);
                    }
                } catch (Exception ex) { windowsCapture.Cancel(); RestoreAfterCapture(); Error(ex); }
            }; launch.Start();
        }
        void RestoreAfterCapture() {
            if (store.Items.Count > 0 && (display.ManualHeld || line.Pinned)) line.Reveal(Forms.Screen.FromPoint(Forms.Cursor.Position), true);
        }
        void Import() {
            line.Busy = true;
            try {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "把图片挂上晾衣绳", Multiselect = true, Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
                if (dialog.ShowDialog() == true) foreach (string file in dialog.FileNames) {
                    try { if (store.ImportFile(file)) NewPhoto(); } catch (Exception ex) { Error(ex); }
                }
            } finally { line.Busy = false; line.KeepUntil = DateTime.Now.AddSeconds(3); }
        }
        Forms.ToolStripMenuItem Item(string label, Action action) {
            var item = new Forms.ToolStripMenuItem(label); item.Click += delegate { try { action(); } catch (Exception ex) { Error(ex); } }; return item;
        }
        void BuildMenu() {
            if (menu != null) menu.Dispose();
            menu = new Forms.ContextMenuStrip();
            menu.Items.Add(Item("展开 / 收起    " + HotkeyBinding.Label(store.Settings.HotkeyModifiers, store.Settings.HotkeyKey), Toggle));
            menu.Items.Add(Item("设置展开 / 收起快捷键…", ConfigureHotkey));
            var capture = Item("截图", Capture); menu.Items.Add(capture); menu.Items.Add(Item("添加图片…", Import));
            menu.Opening += delegate { capture.Text = SnipasteCommands.RunningExecutable() != null
                ? "Snipaste 截图    " + SnipasteCommands.HotkeyLabel() : "Windows 截图    Win + Shift + S"; };
            menu.Items.Add(new Forms.ToolStripSeparator());
            var autoHide = Item("自动隐藏（默认开启）", delegate { line.Pinned = !line.Pinned; });
            autoHide.Checked = store.Settings.AutoHide; menu.Items.Add(autoHide);
            var clip = Item("接收剪贴板图片", delegate {
                bool old = store.Settings.ClipboardEnabled;
                store.Settings.ClipboardEnabled = !old;
                try { store.Save(); } catch { store.Settings.ClipboardEnabled = old; throw; }
                clipboardPending = false; clipboardSequence = Native.GetClipboardSequenceNumber(); BuildMenu();
            }); clip.Checked = store.Settings.ClipboardEnabled; menu.Items.Add(clip);
            var snip = Item("接收 Snipaste 钉在桌面的图片", delegate {
                bool old = store.Settings.SnipasteEnabled; store.Settings.SnipasteEnabled = !old;
                try { store.Save(); } catch { store.Settings.SnipasteEnabled = old; throw; }
                ConfigureSnipaste(); BuildMenu();
            }); snip.Checked = store.Settings.SnipasteEnabled; menu.Items.Add(snip);
            var status = new Forms.ToolStripMenuItem(snipaste != null && snipaste.HistoryFolder != null ? "Snipaste 已连接（保留桌面贴图）" : "Snipaste：启动后自动检测");
            status.Enabled = false; status.ToolTipText = snipaste == null ? "" : snipaste.HistoryFolder; menu.Items.Add(status);
            menu.Items.Add(Item("选择 Snipaste 目录…", delegate {
                line.Busy = true;
                try {
                    using (var dialog = new Forms.FolderBrowserDialog { Description = "选择 Snipaste 安装目录或 history 目录。只读取贴图，保留 Snipaste 原文件。" }) {
                        if (dialog.ShowDialog() == Forms.DialogResult.OK) {
                            string old = store.Settings.SnipasteFolder; store.Settings.SnipasteFolder = dialog.SelectedPath;
                            try { store.Save(); } catch { store.Settings.SnipasteFolder = old; throw; } ConfigureSnipaste(); BuildMenu();
                        }
                    }
                } finally { line.Busy = false; }
            }));
            menu.Items.Add(Item("恢复 Snipaste 自动检测", delegate {
                string old = store.Settings.SnipasteFolder; store.Settings.SnipasteFolder = "";
                try { store.Save(); } catch { store.Settings.SnipasteFolder = old; throw; } ConfigureSnipaste(); BuildMenu();
            }));
            menu.Items.Add(Item("重置晾衣绳高度", delegate {
                double old = store.Settings.HeightOffset; store.Settings.HeightOffset = 0;
                try { store.Save(); } catch { store.Settings.HeightOffset = old; throw; }
                if (line.Revealed) line.Place(Forms.Screen.FromHandle(line.Handle));
            }));
            var edge = Item("屏幕右侧小把手唤出", delegate {
                bool old = store.Settings.EdgeEnabled; store.Settings.EdgeEnabled = !old;
                try { store.Save(); } catch { store.Settings.EdgeEnabled = old; throw; } BuildMenu();
            }); edge.Checked = store.Settings.EdgeEnabled; menu.Items.Add(edge);
            menu.Items.Add(Item("选择截图监听文件夹…", delegate {
                line.Busy = true;
                try {
                    using (var dialog = new Forms.FolderBrowserDialog { Description = "接收此文件夹中新建的图片。", ShowNewFolderButton = true }) {
                        if (dialog.ShowDialog() == Forms.DialogResult.OK) {
                            string old = store.Settings.WatchFolder; store.Settings.WatchFolder = dialog.SelectedPath;
                            try { store.Save(); } catch { store.Settings.WatchFolder = old; throw; } ConfigureWatcher();
                        }
                    }
                } finally { line.Busy = false; }
            }));
            menu.Items.Add(Item("停止接收文件夹图片", delegate {
                string old = store.Settings.WatchFolder; store.Settings.WatchFolder = "";
                try { store.Save(); } catch { store.Settings.WatchFolder = old; throw; } ConfigureWatcher();
            }));
            var startup = Item("开机启动（登录后 15 秒）", ToggleStartup); startup.Checked = StartupEnabled(); menu.Items.Add(startup);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item("清空晾衣绳", delegate {
                line.Busy = true;
                try { store.ClearForExit(); display.CollapseManually(); }
                finally { line.Busy = false; }
            }));
            menu.Items.Add(Item("打开缓存文件夹", delegate { Process.Start("explorer.exe", "\"" + store.Cache + "\""); }));
            menu.Items.Add(Item("使用说明", delegate {
                line.Busy = true;
                try { MessageBox.Show("使用 Snipaste 原快捷键截图，选择钉在桌面后才上晾衣绳。\n直接复制不上绳；编辑后钉图只接收最终版本。\n没有运行 Snipaste 时，托盘可使用 Windows 截图。\n系统截图需复制到剪贴板；Esc 取消，2 分钟后停止本次接收。\n\n左键复制，右键查看。\nShift + 右键打开更多操作，可另存需要保留的图片。\n拖到另一张图上交换位置，拖绳调整高度。\n\n右侧小把手停留 0.3 秒或单击即可展开。\n鼠标留在挂绳区保持显示，离开 1.2 秒收起。\n钉图成功后展开确认，快捷键展开后保持显示。\n没有图片时，绳子和小把手都隐藏。\nSnipaste 桌面贴图可自由拖动对比。\n拖出绳外松手、清空或退出只清理晒图缓存。\nSnipaste 贴图及原文件由 Snipaste 管理。", "晒图使用说明"); }
                finally { line.Busy = false; }
            }));
            menu.Items.Add(Item("关于晒图", delegate {
                line.Busy = true;
                MessageBox.Show("晒图 0.5.8\n\n个人截图暂存工具", "关于晒图");
                line.Busy = false;
            }));
            menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add(Item("清理截图并退出", ExitFromTray));
            menu.Opened += delegate { line.Busy = true; }; menu.Closed += delegate { line.Busy = false; line.KeepUntil = DateTime.Now.AddSeconds(1); };
            tray.ContextMenuStrip = menu;
        }
        void ConfigureHotkey() {
            line.Busy = true;
            try {
                var dialog = new HotkeyDialog(store.Settings.HotkeyModifiers, store.Settings.HotkeyKey,
                    delegate(uint modifiers, uint key) { string failure; return line.ChangeHotkey(modifiers, key, out failure) ? "" : failure; });
                dialog.ShowDialog(); BuildMenu();
            } finally { line.Busy = false; }
        }
        void ExitFromTray() {
            line.Busy = true;
            try { store.ClearForExit(); Cleanup(); app.Shutdown(); }
            catch (Exception ex) { line.Busy = false; Error(ex); }
        }
        bool StartupEnabled() {
            try { return new StartupRegistration(Forms.Application.ExecutablePath).Enabled; }
            catch { return false; }
        }
        void ToggleStartup() {
            var startup = new StartupRegistration(Forms.Application.ExecutablePath);
            if (startup.Enabled) startup.Disable(); else startup.Enable();
            StartupRegistration.RemoveLegacy();
            BuildMenu();
        }
        void Error(Exception ex) {
            if (store != null) store.Log(ex);
            if (tray != null && !closing && !initializing) tray.ShowBalloonTip(5000, "晒图", ex.Message, Forms.ToolTipIcon.Warning);
        }
        void Cleanup() {
            if (closing) return; closing = true;
            if (timer != null) timer.Stop(); if (watcher != null) watcher.Dispose();
            if (snipaste != null) snipaste.Dispose();
            if (edgeHandle != null) edgeHandle.Stop();
            if (line != null) { line.Cleanup(); line.Close(); }
            if (tray != null) { var icon = tray.Icon; tray.Visible = false; tray.Dispose(); if (icon != null) icon.Dispose(); } if (menu != null) menu.Dispose();
        }
    }
}
