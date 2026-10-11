using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ShaiTu {
    public static class SelfTest {
        static List<string> results = new List<string>();
        static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); results.Add("PASS: " + name); }
        public static int Run(string output) {
            output = Path.GetFullPath(output); Directory.CreateDirectory(output);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            string sandbox = Path.Combine(output, "test-runs", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(sandbox);
            int exit = 0;
            try {
                string startupPath = Path.Combine(sandbox, "中文 & 空格", "ShaiTu.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(startupPath));
                File.Copy(typeof(Program).Assembly.Location, startupPath);
                var startup = new StartupRegistration(startupPath, Path.Combine(sandbox, "isolated-startup"));
                Check(!startup.Exists && !startup.Enabled, "Missing startup shortcut is reported disabled");
                startup.Enable();
                Check(startup.Enabled && startup.MatchesPath,
                    "Real Shell shortcut preserves Unicode, spaces, target, startup arguments and working directory");
                string movedPath = Path.Combine(sandbox, "moved", "ShaiTu.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(movedPath)); File.Copy(startupPath, movedPath);
                var movedStartup = new StartupRegistration(movedPath, Path.GetDirectoryName(startup.Shortcut));
                Check(!movedStartup.MatchesPath, "Moving the executable is detected instead of reporting a stale path as current");
                movedStartup.Enable();
                Check(movedStartup.MatchesPath && !startup.MatchesPath, "Re-enabling startup repairs the executable path");
                movedStartup.Disable();
                Check(!startup.Exists && !startup.Enabled, "Disabling startup removes its shortcut");
                Check(StartupRegistration.ApprovalAllows(null) && StartupRegistration.ApprovalAllows(new byte[] { 2 })
                    && !StartupRegistration.ApprovalAllows(new byte[] { 3 }) && !StartupRegistration.ApprovalAllows(new byte[] { 7 }),
                    "Windows-disabled startup entries are reported disabled rather than merely checking file existence");
                Check(StartupRegistration.Arguments == "--startup" && StartupRegistration.DelayMilliseconds == 15000,
                    "Only startup launches use the fifteen-second delay");
                var store = new Store(Path.Combine(sandbox, "state"));
                Check(store.Items.Count == 0 && !store.Settings.ClipboardEnabled && store.Settings.AutoHide && store.Settings.SnipasteEnabled, "Fresh state uses Snipaste and automatic hiding without incidental clipboard imports");
                string legacy = Path.Combine(sandbox, "legacy"); Directory.CreateDirectory(legacy);
                File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"AutoHide\":false,\"ClipboardEnabled\":true,\"EdgeEnabled\":false,\"Files\":[]}");
                var migrated = new Store(legacy);
                Check(migrated.Settings.AutoHide && migrated.Settings.EdgeEnabled && migrated.Settings.InteractionVersion == 3, "Resident release migrates once to region activation and automatic hiding");
                Check(migrated.Settings.HotkeyModifiers == 3 && migrated.Settings.HotkeyKey == 84, "Older settings receive a valid default toggle hotkey");
                Check(!migrated.Settings.ClipboardEnabled && migrated.Settings.CapturePolicyVersion == 1, "Old clipboard defaults migrate once to Snipaste-only reception");
                migrated.Settings.ClipboardEnabled = true; migrated.Save();
                Check(new Store(legacy).Settings.ClipboardEnabled, "Explicitly enabling clipboard reception after migration survives restart");
                var samples = new[] { DrawSample(0), DrawSample(1), DrawSample(2) };
                var fallback = new WindowsCaptureSession(); var fallbackStore = new Store(Path.Combine(sandbox, "windows-capture"));
                DateTime captureTime = DateTime.UtcNow; int launches = 0, reads = 0;
                Func<BitmapSource> readFallback = delegate { reads++; return samples[0]; };
                Action<BitmapSource> saveFallback = delegate(BitmapSource image) { fallbackStore.ImportImage(image, "Windows 截图"); };
                Check(!fallback.TryReceive(captureTime, 41, 99, "SnippingTool", readFallback, saveFallback) && reads == 0,
                    "Windows screenshots are not received without an explicit fallback request");
                fallback.Start(captureTime, 40, delegate { launches++; });
                Check(launches == 1 && fallback.Active && !fallback.TryReceive(captureTime, 40, 99, "SnippingTool", readFallback, saveFallback),
                    "Fallback launch arms one request but ignores the image already on the clipboard");
                Check(!fallback.TryReceive(captureTime, 99, 99, "SnippingTool", readFallback, saveFallback),
                    "Fallback ignores photos copied by ShaiTu itself");
                Check(!fallback.TryReceive(captureTime, 41, 99, "Snipaste", readFallback, saveFallback)
                    && !fallback.TryReceive(captureTime, 41, 99, "chrome", readFallback, saveFallback)
                    && !fallback.TryReceive(captureTime, 41, 99, "unknown", readFallback, saveFallback) && reads == 0,
                    "Fallback excludes Snipaste copies and unrelated clipboard owners");
                Check(!fallback.TryReceive(captureTime, 41, 99, "ScreenClippingHost", delegate { return null; }, saveFallback) && fallback.Active,
                    "Text or incomplete clipboard data does not consume the screenshot request");
                bool fallbackFailed = false;
                fallbackStore.Save();
                using (var lockFile = new FileStream(Path.Combine(fallbackStore.Root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                    try { fallback.TryReceive(captureTime, 41, 99, "SnippingTool", readFallback, saveFallback); }
                    catch (IOException) { fallbackFailed = true; }
                }
                Check(fallbackFailed && fallback.Active && fallbackStore.Items.Count == 0,
                    "Failed Windows screenshot persistence leaves the request retryable and no orphan photo");
                Check(fallback.TryReceive(captureTime, 41, 99, "snippingtool", readFallback, saveFallback)
                    && !fallback.Active && fallbackStore.Items.Count == 1 && !fallbackStore.Settings.ClipboardEnabled,
                    "A Windows screenshot saves once without enabling permanent clipboard reception");
                Check(!fallback.TryReceive(captureTime, 42, 99, "SnippingTool", readFallback, saveFallback) && fallbackStore.Items.Count == 1,
                    "Later Windows clipboard updates cannot append a second screenshot to the completed request");
                fallback.Start(captureTime, 42, delegate { }); fallback.Cancel();
                Check(!fallback.TryReceive(captureTime, 43, 99, "ScreenClippingHost", readFallback, saveFallback),
                    "Cancelling a Windows screenshot prevents a late result from being imported");
                fallback.Start(captureTime, 43, delegate { });
                Check(!fallback.Expire(captureTime.AddSeconds(119)) && fallback.Expire(captureTime.AddMinutes(2)) && !fallback.Active
                    && !fallback.TryReceive(captureTime.AddMinutes(2), 44, 99, "SnippingTool", readFallback, saveFallback),
                    "Abandoned fallback requests expire after two minutes and reject late clipboard images");
                bool launchFailed = false;
                try { fallback.Start(captureTime, 44, delegate { throw new InvalidOperationException("test launch failure"); }); }
                catch (InvalidOperationException) { launchFailed = true; }
                Check(launchFailed && !fallback.Active, "A failed Windows screenshot launch never leaves clipboard reception armed");
                Check(WindowsSnipCommands.NativeInputSize == (IntPtr.Size == 8 ? 40 : 28),
                    "Windows screenshot input has the correct native layout for the current process architecture");
                Check(store.ImportImage(samples[0]), "Import clipboard bitmap into private snapshot");
                Check(File.ReadAllText(Path.Combine(store.Root, "captures.log")).Contains("600x") && File.ReadAllText(Path.Combine(store.Root, "captures.log")).Contains(Path.GetFileName(store.Items[0].File)), "Capture audit connects image dimensions and reception source to the owned snapshot");
                Check(!store.ImportImage(samples[0]) && store.Items.Count == 1, "Duplicate bitmap does not add another item");
                var pixelStore = new Store(Path.Combine(sandbox, "format-dedup"));
                var pixel32 = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 }, 8);
                var pixel24 = BitmapSource.Create(2, 1, 144, 144, PixelFormats.Bgr24, null, new byte[] { 30, 20, 10, 60, 50, 40 }, 6);
                pixelStore.ImportImage(pixel32);
                Check(!pixelStore.ImportImage(pixel24), "Equivalent Snipaste/clipboard pixels deduplicate across bitmap formats and DPI");
                string original = Path.Combine(sandbox, "original.png"); WritePng(samples[1], original);
                Check(store.ImportFile(original), "File import");
                Photo p = store.Items.Last(); var data = LineWindow.DragData(p);
                Check(data.ContainsFileDropList() && data.GetFileDropList()[0] == p.File && data.ContainsImage(), "Drag payload contains file and full bitmap");
                var loaded = new Store(store.Root);
                Check(loaded.Items.Count == 2 && loaded.Items[1].Width == 600, "Snapshots restored after restart");
                Photo left = loaded.Items[0], right = loaded.Items[1];
                Check(loaded.Swap(left, right) && loaded.Items[0] == right && loaded.Items[1] == left, "Drag reorder swaps two positions");
                Check(Path.GetFileName(new Store(loaded.Root).Items[0].File) == Path.GetFileName(right.File), "Swapped order persists across restart");
                Check(!loaded.Swap(right, right), "Dropping a photo onto itself changes nothing");
                loaded.Swap(right, left);
                loaded.Remove(loaded.Items.Last());
                Check(File.Exists(original) && !File.Exists(p.File) && new Store(store.Root).Items.Count == 1, "Removing snapshot preserves original and persists");
                loaded.Clear();
                for (int i = 0; i < 10; i++) loaded.ImportImage(DrawSample(i + 3));
                Check(loaded.Items.Count == 8 && Directory.GetFiles(loaded.Cache, "*.png").Length == 8, "Capacity evicts oldest snapshots and bounds disk cache");
                string first = loaded.Items[0].File;
                using (var lockFile = new FileStream(Path.Combine(loaded.Root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                    bool failed = false; try { loaded.Remove(loaded.Items[0]); } catch (IOException) { failed = true; }
                    Check(failed && loaded.Items.Count == 8 && File.Exists(first), "Failed persistence rolls back removal");
                    failed = false; try { loaded.ImportImage(DrawSample(98)); } catch (IOException) { failed = true; }
                    Check(failed && loaded.Items.Count == 8 && Directory.GetFiles(loaded.Cache, "*.png").Length == 8, "Failed persistence rolls back import and removes orphan");
                }
                Check(!Store.IsOwnedName("..\\test.png") && !Store.IsOwnedName("photo.png") && Store.IsOwnedName(Guid.NewGuid().ToString("N") + ".png"), "Only owned GUID filenames are eligible for cleanup");
                using (var lockFile = new FileStream(Path.Combine(loaded.Root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                    Photo[] before = loaded.Items.ToArray(); bool failed = false;
                    try { loaded.Swap(before[0], before[1]); } catch (IOException) { failed = true; }
                    Check(failed && loaded.Items.SequenceEqual(before), "Failed persistence rolls back a position swap");
                }
                loaded.Settings.HeightOffset = 180; loaded.Settings.AutoHide = false; loaded.Save();
                var positionState = new Store(loaded.Root);
                Check(positionState.Settings.HeightOffset == 180 && !positionState.Settings.AutoHide, "Height and resident display mode persist");
                loaded.Settings.HotkeyModifiers = 0; loaded.Settings.HotkeyKey = 119; loaded.Save();
                Check(new Store(loaded.Root).Settings.HotkeyKey == 119 && new Store(loaded.Root).Settings.HotkeyModifiers == 0,
                    "A custom single F8 key survives restart");
                Check(HotkeyBinding.IsValid(7, 84) && HotkeyBinding.IsValid(0, 175) && !HotkeyBinding.IsValid(0, 16) && !HotkeyBinding.IsValid(16, 84), "Single/media/combo keys accepted; bare modifiers and invalid masks rejected");
                Check(LineWindow.ClampOffset(-20, 1000, 1) == 0 && LineWindow.ClampOffset(900, 1000, 1) == 750 && LineWindow.ClampOffset(400, 600, 2) == 50, "Height stays inside the working area at mixed DPI");
                string invalid = Path.Combine(sandbox, "broken.png"); File.WriteAllText(invalid, "not a PNG");
                bool rejected = false; try { loaded.ImportFile(invalid); } catch { rejected = true; }
                Check(rejected && loaded.Items.Count == 8, "Corrupt image cannot alter persisted state");
                var portrait = new RenderTargetBitmap(64, 2048, 96, 96, PixelFormats.Pbgra32);
                var portraitVisual = new DrawingVisual(); using (var dc = portraitVisual.RenderOpen()) dc.DrawRectangle(Brushes.CadetBlue, null, new Rect(0, 0, 64, 2048));
                portrait.Render(portraitVisual); string portraitPath = Path.Combine(sandbox, "portrait.png"); WritePng(portrait, portraitPath);
                var portraitThumb = Store.ReadImage(portraitPath, true);
                Check(portraitThumb.PixelHeight <= 480 && portraitThumb.PixelWidth < 64, "Portrait thumbnail is bounded on its longest side");
                string snipHistory = Path.Combine(sandbox, "snipaste-history"); Directory.CreateDirectory(snipHistory);
                WriteSnip(samples[0], Path.Combine(snipHistory, "old.sp1"));
                WritePng(samples[0], Path.Combine(snipHistory, "old-pin.png"));
                int snipCount = 0;
                var snip = new SnipasteBridge(delegate(BitmapSource image) { if (image.PixelWidth == 600) snipCount++; }, delegate(Exception e) { throw e; });
                snip.Configure(snipHistory);
                for (int n = 0; n < 5; n++) snip.Tick();
                Check(snipCount == 0, "Snipaste connection does not import old pins or screenshot history");
                WriteSnip(samples[1], Path.Combine(snipHistory, "new.sp1"));
                for (int n = 0; n < 5; n++) snip.Tick();
                Check(snipCount == 0, "Copy-only Snipaste capture history never imports a photo");
                WritePng(samples[1], Path.Combine(snipHistory, "new-pin.png"));
                snip.Tick(true);
                Check(snipCount == 1, "A final pinned PNG imports without reading the clipboard or screen");
                var editedStore = new Store(Path.Combine(sandbox, "edited-pin-state"));
                string editedHistory = Path.Combine(sandbox, "edited-pin-history"); Directory.CreateDirectory(editedHistory);
                int pinAcks = 0; var pinOnly = new SnipasteBridge(delegate(BitmapSource image) { editedStore.ImportImage(image); }, delegate(Exception e) { throw e; });
                pinOnly.CaptureAccepted += delegate(string file) { pinAcks++; };
                pinOnly.Configure(editedHistory);
                string rawHistory = Path.Combine(editedHistory, "before-edit.sp1");
                WriteSnip(samples[1], rawHistory); pinOnly.Tick(true);
                Check(editedStore.Items.Count == 0 && pinAcks == 0,
                    "An unedited history record causes no import or pin acknowledgment");
                string finalPin = Path.Combine(editedHistory, "edited-pin.png"); WritePng(samples[2], finalPin); pinOnly.Tick(true);
                Check(editedStore.Items.Count == 1 && editedStore.HasSnapshot(samples[2]) && !editedStore.HasSnapshot(samples[1]) && pinAcks == 1,
                    "Editing then pinning imports exactly one photo containing the final edited pixels");
                WriteSnip(samples[2], rawHistory); pinOnly.Tick(true);
                Check(editedStore.Items.Count == 1 && pinAcks == 1,
                    "Late history writes cannot add or acknowledge SP1 records");
                Check(File.Exists(finalPin) && File.Exists(rawHistory) && editedStore.Items[0].File != finalPin,
                    "A native pin and its independent clothesline snapshot coexist after receipt");
                string snapshot = editedStore.Items[0].File;
                editedStore.Remove(editedStore.Items[0]);
                Check(!File.Exists(snapshot) && File.Exists(finalPin) && File.Exists(rawHistory),
                    "Removing the clothesline snapshot leaves the native pin image and history intact");
                File.SetLastWriteTimeUtc(finalPin, DateTime.UtcNow.AddSeconds(3)); pinOnly.Tick(true);
                Check(editedStore.Items.Count == 0 && pinAcks == 1,
                    "Native pin timestamp changes cannot rehang an unchanged removed image");
                WritePng(samples[2], finalPin); pinOnly.Tick(true);
                Check(editedStore.Items.Count == 0 && pinAcks == 1,
                    "Rewriting identical native pixels cannot rehang an unchanged removed image");
                WritePng(samples[0], finalPin); pinOnly.Tick(true);
                Check(editedStore.Items.Count == 1 && editedStore.HasSnapshot(samples[0]) && pinAcks == 2,
                    "A changed native image is still received as a new final version");
                editedStore.ClearForExit();
                Check(File.Exists(finalPin) && File.Exists(rawHistory) && new Store(editedStore.Root).Items.Count == 0,
                    "Exit clears clothesline snapshots while leaving native pin files available");
                pinOnly.Dispose();
                string regionHistory = Path.Combine(sandbox, "region.sp1");
                var selectedRegion = new Int32Rect(171, 84, 137, 96);
                WriteSnip(samples[1], regionHistory, selectedRegion);
                var actualRegion = SnipasteBridge.ReadCapture(regionHistory);
                var expectedRegion = new CroppedBitmap(samples[1], selectedRegion);
                var regionStore = new Store(Path.Combine(sandbox, "region-dedup")); regionStore.ImportImage(expectedRegion, "Snipaste:pin.png");
                Check(actualRegion.PixelWidth == 137 && actualRegion.PixelHeight == 96 && !regionStore.ImportImage(actualRegion, "Snipaste:region.sp1") && regionStore.Items.Count == 1,
                    "One selected capture represented by pin PNG and full-desktop SP1 produces exactly one cropped photo");
                WriteSnip(samples[1], regionHistory, selectedRegion, -1200, -400);
                Check(regionStore.HasSnapshot(SnipasteBridge.ReadCapture(regionHistory)), "History selection subtracts a negative desktop origin correctly");
                byte[] partialHistory = File.ReadAllBytes(regionHistory); Array.Resize(ref partialHistory, partialHistory.Length - 20);
                bool unfinishedRejected = false; try { SnipasteBridge.DecodeHistory(partialHistory); } catch (InvalidDataException) { unfinishedRejected = true; }
                Check(unfinishedRejected, "A history backing image without complete selection metadata is never imported as an extra desktop photo");
                WriteSnip(samples[1], regionHistory, new Int32Rect(590, 400, 80, 90));
                bool boundsRejected = false; try { SnipasteBridge.ReadCapture(regionHistory); } catch (InvalidDataException) { boundsRejected = true; }
                Check(boundsRejected, "Out-of-bounds selection is rejected rather than falling back to the whole desktop");
                for (int n = 0; n < 10; n++) snip.Tick();
                Check(snipCount == 1, "Unchanged Snipaste capture is received once");
                snip.Dispose();
                bool invalidSnip = false; try { SnipasteBridge.ExtractPng(new byte[64]); } catch (InvalidDataException) { invalidSnip = true; }
                Check(invalidSnip, "Unknown Snipaste history formats are rejected");
                Check(SnipasteCommands.ParseHotkey("\"16777330, 175\"") == "音量+" && SnipasteCommands.ParseHotkey("100663361, 65") == "Ctrl + Shift + A", "Existing Snipaste media-key and modifier hotkeys are displayed without changing configuration");
                DateTime acceptedAt = new DateTime(2026, 10, 10);
                var failedHistory = Path.Combine(sandbox, "failed-receive"); Directory.CreateDirectory(failedHistory);
                int acks = 0; bool persistAllowed = false;
                var failedBridge = new SnipasteBridge(delegate(BitmapSource image) {
                    if (!persistAllowed) throw new IOException("simulated persistence failure");
                    store.ImportImage(image);
                }, delegate(Exception e) { throw e; });
                failedBridge.CaptureAccepted += delegate { acks++; };
                failedBridge.Configure(failedHistory); WritePng(samples[2], Path.Combine(failedHistory, "new-pin.png"));
                for (int n = 0; n < 5; n++) failedBridge.Tick();
                Check(acks == 0, "Failed image persistence never acknowledges or hides Snipaste pins");
                persistAllowed = true; for (int n = 0; n < 5; n++) failedBridge.Tick();
                Check(acks == 1 && failedBridge.LatestAlreadyStored(store), "Successful retry acknowledges saved capture and restart recognizes its existing pin");
                failedBridge.Dispose();
                var dismissal = new PointerDismissal();
                Check(!dismissal.Update(acceptedAt, false, false) && !dismissal.Update(acceptedAt.AddMilliseconds(1100), false, false)
                    && dismissal.Update(acceptedAt.AddMilliseconds(1201), false, false), "Pointer can cross outside the panel without hiding until 1.2 seconds elapse");
                dismissal.Reset(); dismissal.Update(acceptedAt, false, false);
                Check(!dismissal.Update(acceptedAt.AddMilliseconds(1000), true, false) && !dismissal.Update(acceptedAt.AddSeconds(20), true, false)
                    && !dismissal.Update(acceptedAt.AddSeconds(21), false, false), "Returning to photos cancels dismissal and hovering keeps them stable indefinitely");
                Check(!dismissal.Update(acceptedAt.AddHours(1), false, true) && !dismissal.Update(acceptedAt.AddHours(1).AddSeconds(1), false, false),
                    "Interaction or manual hold clears the old leave timer instead of hiding immediately when released");
                var fakePointer = new System.Drawing.Point(-10000,-10000);
                var edgePreview = new EdgeHandleWindow(delegate { return fakePointer; }); int clicks = 0; edgePreview.OpenRequested += delegate { clicks++; };
                new System.Windows.Interop.WindowInteropHelper(edgePreview).EnsureHandle();
                edgePreview.Grip.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                Check(clicks == 1 && !edgePreview.ShowActivated && !edgePreview.ShowInTaskbar, "A single edge-handle click requests opening without focus or taskbar clutter");
                var testLine = new LineWindow(store, delegate(Exception ex) { throw ex; }, true);
                testLine.Place(System.Windows.Forms.Screen.PrimaryScreen);
                edgePreview.Sync(true, System.Windows.Forms.Screen.PrimaryScreen, testLine.Handle);
                var edgeRect = edgePreview.Region(); fakePointer = new System.Drawing.Point(edgeRect.Left + edgeRect.Width/2, edgeRect.Top + edgeRect.Height/2);
                edgePreview.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0) { RoutedEvent = UIElement.MouseLeaveEvent });
                var dwellWatch = System.Diagnostics.Stopwatch.StartNew();
                edgePreview.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0) { RoutedEvent = UIElement.MouseEnterEvent });
                Wait(delegate { return clicks == 2; },1200);
                Check(clicks == 2 && dwellWatch.ElapsedMilliseconds >= 280, "Real dispatcher dwell opens after 0.3 seconds instead of immediately on pointer entry");
                edgePreview.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0) { RoutedEvent = UIElement.MouseEnterEvent });
                fakePointer = new System.Drawing.Point(-10000,-10000);
                edgePreview.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0) { RoutedEvent = UIElement.MouseLeaveEvent });
                Wait(delegate { return clicks > 2; },450);
                Check(clicks == 2, "Crossing and leaving the handle before dwell completes never opens the photos");
                testLine.Close();
                edgePreview.Grip.Measure(new Size(18,64)); edgePreview.Grip.Arrange(new Rect(0,0,18,64));
                var gripBitmap = new RenderTargetBitmap(36,128,192,192,PixelFormats.Pbgra32); gripBitmap.Render(edgePreview.Grip);
                WritePng(gripBitmap, Path.Combine(output,"edge-handle-preview.png")); edgePreview.Stop();
                var display = new DisplaySession();
                Check(display.AcceptCapture(acceptedAt, false) && display.HoldVisible(acceptedAt.AddSeconds(4)) && !display.HoldVisible(acceptedAt.AddSeconds(6)), "Accepted screenshot immediately reveals with a five-second confirmation window");
                display.ExpandManually(); display.AcceptCapture(acceptedAt, false);
                Check(display.HoldVisible(acceptedAt.AddHours(12)), "Screenshot and timeout never release a manually expanded session");
                display.CollapseManually();
                Check(!display.HoldVisible(acceptedAt.AddSeconds(1)), "Manual collapse cancels both confirmation and manual holding");
                Check(!display.AcceptCapture(acceptedAt, true) && display.CapturePending && display.ResumeCapture(acceptedAt.AddMinutes(1)) && display.HoldVisible(acceptedAt.AddMinutes(1).AddSeconds(4)), "Fullscreen snip delays confirmation until it can be shown");
                string cleanupHistory = Path.Combine(sandbox, "cleanup-history"); Directory.CreateDirectory(cleanupHistory);
                var cleanupStore = new Store(Path.Combine(sandbox, "cleanup-state")); cleanupStore.ImportFile(original);
                cleanupStore.Settings.HeightOffset = 65; cleanupStore.Settings.HotkeyModifiers = 0; cleanupStore.Settings.HotkeyKey = 119;
                string acknowledged = Path.Combine(cleanupHistory, "accepted.sp1"), reused = Path.Combine(cleanupHistory, "reused.sp1"), untouched = Path.Combine(cleanupHistory, "untouched.sp1");
                WriteSnip(samples[0], acknowledged); WriteSnip(samples[0], reused); WriteSnip(samples[2], untouched);
                WriteSnip(samples[1], reused);
                string unknown = Path.Combine(cleanupStore.Cache, "user-saved.png"), orphan = Path.Combine(cleanupStore.Cache, Guid.NewGuid().ToString("N") + ".png");
                WritePng(samples[0], unknown); WritePng(samples[0], orphan);
                using (var lockSettings = new FileStream(Path.Combine(cleanupStore.Root, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None)) {
                    bool failed = false; try { cleanupStore.ClearForExit(); } catch (IOException) { failed = true; }
                    Check(failed && cleanupStore.Items.Count == 1 && File.Exists(cleanupStore.Items[0].File), "Failed exit persistence keeps the app snapshot available");
                }
                cleanupStore.ClearForExit();
                Check(File.Exists(acknowledged) && File.Exists(reused) && File.Exists(untouched), "Exit leaves all native Snipaste images and history intact");
                Check(File.Exists(original) && File.Exists(unknown) && !File.Exists(orphan) && new Store(cleanupStore.Root).Items.Count == 0, "Exit purges owned snapshots/orphans and preserves explicitly saved source files");
                var exitState = new Store(cleanupStore.Root);
                Check(exitState.Settings.HotkeyKey == 119 && exitState.Settings.HeightOffset == 65 && File.ReadAllText(Path.Combine(cleanupStore.Root, "settings.json.bak")).Contains("\"Files\":[]"), "Exit clears primary/backup capture lists but retains settings");
                Check(!File.Exists(Path.Combine(cleanupStore.Root, "captures.log")), "Exit also removes capture source audit records");
                string legacyRoot = Path.Combine(sandbox, "legacy-source-queue"); Directory.CreateDirectory(legacyRoot);
                string legacySource = Path.Combine(cleanupHistory, "legacy-pin.png"); WritePng(samples[0], legacySource);
                string legacyJson = "{\"HeightOffset\":65,\"SnipasteSources\":[{\"File\":\"" + legacySource.Replace("\\", "\\\\")
                    + "\",\"HistoryRoot\":\"" + cleanupHistory.Replace("\\", "\\\\") + "\",\"Fingerprint\":\"obsolete\"}]}";
                File.WriteAllText(Path.Combine(legacyRoot, "settings.json"), legacyJson);
                var legacyStore = new Store(legacyRoot); legacyStore.ClearForExit();
                Check(File.Exists(legacySource) && legacyStore.Settings.HeightOffset == 65
                    && !File.ReadAllText(Path.Combine(legacyRoot, "settings.json")).Contains("SnipasteSources")
                    && !File.ReadAllText(Path.Combine(legacyRoot, "settings.json.bak")).Contains("SnipasteSources"),
                    "Legacy source-deletion queues are discarded in both settings files without deleting native images");
                string incoming = Path.Combine(sandbox, "incoming"); Directory.CreateDirectory(incoming);
                WritePng(samples[0], Path.Combine(incoming, "old.png"));
                int seen = 0; var errors = new List<Exception>();
                using (var watcher = new IncomingWatcher(app.Dispatcher, delegate(string file) { Store.ReadImage(file, false); seen++; }, delegate(Exception e) { errors.Add(e); })) {
                    watcher.Configure(new[] { incoming });
                    WritePng(samples[2], Path.Combine(incoming, "new.png"));
                    Wait(delegate { watcher.Tick(); return seen == 1; }, 5000);
                    Check(seen == 1 && errors.Count == 0, "Real filesystem event imports new file and ignores existing files");
                    File.WriteAllText(Path.Combine(incoming, "ignore.txt"), "not an image");
                    Check(!IncomingWatcher.IsImage("ignore.txt") && IncomingWatcher.IsImage("capture.PNG"), "Image-only watcher filter");
                }
                string lateFolder = Path.Combine(sandbox, "created-later");
                using (var watcher = new IncomingWatcher(app.Dispatcher, delegate(string file) { seen++; }, delegate(Exception e) { errors.Add(e); })) {
                    seen = 0; watcher.Configure(new[] { lateFolder }); Directory.CreateDirectory(lateFolder);
                    WritePng(samples[0], Path.Combine(lateFolder, "first-capture.png"));
                    Wait(delegate { watcher.Tick(); return seen == 1; }, 5000);
                    Check(seen == 1 && errors.Count == 0, "First capture in a newly created screenshot directory is received");
                }
                var visualStore = new Store(Path.Combine(sandbox, "visual"));
                foreach (var sample in samples) visualStore.ImportImage(sample);
                var window = new LineWindow(visualStore, delegate(Exception e) { throw e; }, true);
                Render(window, 1280, Path.Combine(output, "line-preview.png"));
                Check(window.Surface.Children.Count == 5 && !window.IsVisible, "Draggable rope and three photos render without a capsule toolbar; startup stays hidden");
                double band = window.ActiveBandHeight(1280);
                Check(band > 140 && band < 184, "Activation band follows actual photo proportions and excludes the former toolbar");
                int previewCount = 0; window.PreviewRequested += delegate(Photo photo) { previewCount++; };
                var host = (System.Windows.Controls.Canvas)window.Surface.Children[2];
                var card = (System.Windows.Controls.Border)host.Children[0];
                var rightClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right) {
                    RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent, Source = card };
                card.RaiseEvent(rightClick);
                Check(previewCount == 1 && rightClick.Handled, "A right mouse click directly previews the image and suppresses the ordinary context menu");
                window.Reveal(System.Windows.Forms.Screen.PrimaryScreen, false);
                host = (System.Windows.Controls.Canvas)window.Surface.Children[2];
                card = (System.Windows.Controls.Border)host.Children[0];
                var photoMenu = card.ContextMenu; photoMenu.PlacementTarget = card; photoMenu.IsOpen = true;
                Wait(delegate { return window.MenuWatcher != null; }, 1000);
                var menuGuard = window.MenuWatcher;
                var menuSource = PresentationSource.FromVisual(photoMenu) as System.Windows.Interop.HwndSource;
                Native.RECT menuRect;
                bool menuPlaced = menuSource != null && Native.GetWindowRect(menuSource.Handle, out menuRect);
                if (!menuPlaced) throw new Exception("Context menu popup was not created.");
                Native.GetWindowRect(menuSource.Handle, out menuRect);
                Check(photoMenu.IsOpen && window.Busy && menuGuard != null && menuGuard.Watching,
                    "Photo menu installs a temporary native click listener while keeping the rope visible");
                menuGuard.LeftButtonDown((menuRect.Left + menuRect.Right) / 2, (menuRect.Top + menuRect.Bottom) / 2);
                Wait(delegate { return false; }, 60);
                Check(photoMenu.IsOpen, "A left click inside the popup leaves its menu commands available");
                menuGuard.LeftButtonDown(menuRect.Right + 100, menuRect.Bottom + 100);
                Wait(delegate { return !photoMenu.IsOpen && window.MenuWatcher == null; }, 1000);
                Check(!photoMenu.IsOpen && !window.Busy && window.MenuWatcher == null && !menuGuard.Watching,
                    "A left click outside the popup closes it and releases the native listener and busy state");
                photoMenu.IsOpen = true; Wait(delegate { return window.MenuWatcher != null; }, 1000);
                menuGuard = window.MenuWatcher; window.Rebuild(false);
                Check(!photoMenu.IsOpen && !menuGuard.Watching && !window.Busy,
                    "Rebuilding the rope closes an open menu without leaking its listener");
                var bounds = window.ActiveRegion();
                var insideDrop = new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + 30);
                var outsideDrop = new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Bottom + 50);
                Photo draggedPhoto = visualStore.Items[0];
                window.FinishPhotoDrag(draggedPhoto, DragDropEffects.None, true, false, outsideDrop);
                window.FinishPhotoDrag(draggedPhoto, DragDropEffects.None, false, false, outsideDrop);
                Check(visualStore.Items.Count == 3 && File.Exists(draggedPhoto.File), "Esc cancellation and a drag without mouse release retain the photo and cache");
                visualStore.Swap(visualStore.Items[0], visualStore.Items[1]);
                window.FinishPhotoDrag(draggedPhoto, DragDropEffects.Link, false, true, insideDrop);
                Check(visualStore.Items.Count == 3 && File.Exists(draggedPhoto.File), "Swapping photos within the rope does not delete either snapshot");
                window.FinishPhotoDrag(draggedPhoto, DragDropEffects.Copy, false, true, outsideDrop);
                Check(visualStore.Items.Count == 2 && !File.Exists(draggedPhoto.File) && new Store(visualStore.Root).Items.Count == 2,
                    "Releasing an externally copied photo outside the rope removes its cache and persists the removal");
                draggedPhoto = visualStore.Items[0];
                window.FinishPhotoDrag(draggedPhoto, DragDropEffects.None, false, true, outsideDrop);
                Check(visualStore.Items.Count == 1 && !File.Exists(draggedPhoto.File), "Dropping outside onto an area with no accepting target also cleans the snapshot");
                foreach (var sample in samples) visualStore.ImportImage(sample);
                Render(window, 1280, Path.Combine(output, "line-preview.png"));
                window.Toast("✓ 已复制"); Render(window, 1280, Path.Combine(output, "copy-toast-preview.png"), false);
                var toast = (System.Windows.Controls.Border)window.Surface.Children[window.Surface.Children.Count - 1];
                Check(System.Windows.Controls.Canvas.GetTop(toast) + toast.DesiredSize.Height + 16 <= window.Height,
                    "Copy feedback and its shadow fit completely inside the transparent window");
                visualStore.Clear(); Render(window, 800, Path.Combine(output, "empty-preview.png"));
                window.Reveal(System.Windows.Forms.Screen.PrimaryScreen, false);
                Check(window.Surface.Children.Count == 0 && window.ActiveBandHeight(800) == 0 && !window.Revealed && !window.IsVisible,
                    "Empty state draws no rope or hint and cannot reveal even when requested");
                for (int i = 0; i < 8; i++) visualStore.ImportImage(DrawSample(i + 3));
                Render(window, 800, Path.Combine(output, "capacity-preview.png"));
                Check(window.Surface.Children.Count == 10 && window.ActiveBandHeight(800) < band, "Eight photos fit an 800 DIP monitor width and activation shrinks with them");
                visualStore.Clear();
                Check(new Store(visualStore.Root).Items.Count == 0 && File.Exists(original), "Clear persists and leaves source file intact");
                window.Close();
                var editor = new HotkeyDialog(3, 84, delegate(uint m, uint k) { return ""; });
                var content = (FrameworkElement)editor.Content; editor.Content = null;
                content.SetValue(System.Windows.Documents.TextElement.FontFamilyProperty, editor.FontFamily);
                var frame = new System.Windows.Controls.Grid { Width = 420, Background = Brushes.White }; frame.Children.Add(content);
                frame.Measure(new Size(420, 600)); double editorHeight = Math.Ceiling(frame.DesiredSize.Height);
                frame.Arrange(new Rect(0, 0, 420, editorHeight)); frame.UpdateLayout();
                var editorBitmap = new RenderTargetBitmap(420, (int)editorHeight, 96, 96, PixelFormats.Pbgra32);
                editorBitmap.Render(frame); WritePng(editorBitmap, Path.Combine(output, "hotkey-editor-preview.png")); editor.Close();
                results.Add("All " + results.Count + " checks passed. Native clipboard delivery, Explorer drop, tray, fullscreen and physical mixed-DPI monitors require interactive verification.");
            } catch (Exception ex) { results.Add(ex.ToString()); exit = 1; }
            File.WriteAllLines(Path.Combine(output, "test-results.txt"), results, System.Text.Encoding.UTF8);
            File.WriteAllLines(Path.Combine(sandbox, "test-results.txt"), results, System.Text.Encoding.UTF8);
            app.Shutdown(); return exit;
        }
        public static int LayoutMetrics(string output) {
            output = Path.GetFullPath(output); Directory.CreateDirectory(output);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var store = new Store(Path.Combine(output, "layout-metrics-state"));
            string userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaiTu");
            using (var stream = File.OpenRead(Path.Combine(userRoot, "settings.json"))) {
                var settings = (Settings)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                store.Settings.HeightOffset = settings.HeightOffset;
                foreach (string name in settings.Files.Where(Store.IsOwnedName).Take(Store.Capacity)) {
                    var image = Store.ReadImage(Path.Combine(userRoot, "images", name), true);
                    // Read only dimensions from metadata, without creating copies of private pictures.
                    using (var imageStream = File.OpenRead(Path.Combine(userRoot, "images", name))) {
                        var frame = BitmapDecoder.Create(imageStream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                        store.Items.Add(new Photo { Width = frame.PixelWidth, Height = frame.PixelHeight, Thumb = image });
                    }
                }
            }
            var window = new LineWindow(store, delegate(Exception e) { throw e; }, true);
            var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position); window.Place(screen);
            double scale = System.Windows.Interop.HwndSource.FromHwnd(window.Handle).CompositionTarget.TransformToDevice.M22;
            var region = window.ActiveRegion();
            File.WriteAllLines(Path.Combine(output, "layout-metrics.txt"), new[] {
                "Screen: " + screen.Bounds.Width + " x " + screen.Bounds.Height,
                "DPI scale: " + scale, "Saved photo count: " + store.Items.Count,
                "Previous window allocation: " + (250 * scale) + " px; " + (250 * scale * 100 / screen.Bounds.Height).ToString("F1") + "% screen height",
                "New activation band: " + window.ActiveBandHeight(window.Width) + " DIP; " + region.Height + " px; " + (region.Height * 100.0 / screen.Bounds.Height).ToString("F1") + "% screen height/area",
                "Band width: " + region.Width + " px; top: " + region.Top + " px", "Snipaste screenshot hotkey: " + SnipasteCommands.HotkeyLabel(),
                "Private user settings and pictures were read only. No desktop overlay shown."
            });
            window.Close(); app.Shutdown(); return 0;
        }
        static void WriteSnip(BitmapSource bitmap, string path, Int32Rect? selection = null, int originX = 0, int originY = 0) {
            byte[] png; var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var ms = new MemoryStream()) { encoder.Save(ms); png = ms.ToArray(); }
            using (var fs = File.Create(path)) {
                fs.Write(new byte[] { 0, 0, 0, 26 }, 0, 4); byte[] header = System.Text.Encoding.BigEndianUnicode.GetBytes("SNIPASTE-SNIP");
                fs.Write(header, 0, header.Length); fs.Write(new byte[] { 0, 0, 0, 201, 0, 0, 0, 1 }, 0, 8); fs.Write(png, 0, png.Length);
                Int32Rect rect = selection ?? new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
                foreach (int number in new[] { originX, originY, rect.X + originX, rect.Y + originY, rect.X + originX + rect.Width - 1, rect.Y + originY + rect.Height - 1 })
                    fs.Write(new byte[] { (byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number }, 0, 4);
            }
        }
        public static int SnipasteLive(string output) {
            Directory.CreateDirectory(output);
            try {
                string folder = SnipasteBridge.Discover();
                if (folder == null) throw new Exception("No local Snipaste history found.");
                string file = Directory.GetFiles(folder, "*.sp1", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).First();
                var image = SnipasteBridge.ReadCapture(file);
                WritePng(image, Path.Combine(output, "snipaste-readback.png"));
                var state = new Store(Path.Combine(output, "snipaste-test-state")); state.ImportImage(image);
                File.WriteAllLines(Path.Combine(output, "snipaste-live-results.txt"), new[] { "PASS: real local Snipaste history read", "History: " + folder,
                    "Source: " + file, "Original pixel dimensions: " + image.PixelWidth + " x " + image.PixelHeight,
                    "Private snapshot persisted: " + File.Exists(state.Items.Last().File), "No Snipaste settings changed; no user clipboard modified." });
                return 0;
            } catch (Exception ex) { File.WriteAllText(Path.Combine(output, "snipaste-live-results.txt"), ex.ToString()); return 1; }
        }
        static void Wait(Func<bool> ready, int milliseconds) {
            var frame = new DispatcherFrame(); var start = DateTime.UtcNow;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += delegate { if (ready() || (DateTime.UtcNow - start).TotalMilliseconds > milliseconds) { timer.Stop(); frame.Continue = false; } };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        static void Render(LineWindow window, double width, string file, bool rebuild = true) {
            window.Width = width; if (rebuild) window.Rebuild(false);
            window.Surface.Width = width; window.Surface.Height = 250;
            window.Surface.Measure(new Size(width, 250)); window.Surface.Arrange(new Rect(0, 0, width, 250)); window.Surface.UpdateLayout();
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(221, 230, 237), Color.FromRgb(238, 225, 212), new Point(0, 0), new Point(1, 1)), null, new Rect(0, 0, width, 250));
                dc.DrawRectangle(new VisualBrush(window.Surface) { ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(0, 0, width, 250), Stretch = Stretch.Fill }, null, new Rect(0, 0, width, 250));
            }
            var target = new RenderTargetBitmap((int)width, 250, 96, 96, PixelFormats.Pbgra32); target.Render(visual); WritePng(target, file);
        }
        static void WritePng(BitmapSource bitmap, string path) { var e = new PngBitmapEncoder(); e.Frames.Add(BitmapFrame.Create(bitmap)); using (var fs = File.Create(path)) e.Save(fs); }
        public static BitmapSource DrawSample(int variant) {
            int width = 600, height = variant == 1 ? 420 : 360; var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) {
                if (variant == 0) {
                    dc.DrawRectangle(LineWindow.Brush("#FAFAF7"), null, new Rect(0, 0, width, height));
                    dc.DrawRectangle(LineWindow.Brush("#24394E"), null, new Rect(0, 0, width, 42));
                    DrawText(dc, "周末的灵感", 32, "#24394E", 36, 68);
                    DrawText(dc, "把想法留下，让桌面呼吸。", 21, "#687B8D", 36, 123);
                    for (int i = 0; i < 4; i++) dc.DrawRoundedRectangle(LineWindow.Brush("#E0E6E9"), null, new Rect(36, 190 + i * 30, 480 - i * 45, 10), 5, 5);
                } else if (variant == 1) {
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(223, 160, 117), Color.FromRgb(243, 211, 163), 90), null, new Rect(0, 0, width, height));
                    dc.DrawEllipse(LineWindow.Brush("#FFF4C6"), null, new Point(420, 116), 52, 52);
                    var mountains = StreamGeometry.Parse("M0,320 Q130,170 290,310 T600,280 L600,420 L0,420 Z");
                    dc.DrawGeometry(LineWindow.Brush("#52736B"), null, mountains);
                    dc.DrawGeometry(LineWindow.Brush("#304E49"), null, StreamGeometry.Parse("M0,390 Q270,270 600,370 L600,420 L0,420 Z"));
                } else {
                    byte c = (byte)(variant * 13 % 70);
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(24 + c), 41, 57)), null, new Rect(0, 0, width, height));
                    DrawText(dc, "这一周", 29, "#F5F7F8", 36, 28);
                    for (int i = 0; i < 7; i++) { double h = 50 + (i * 31 + variant * 7) % 160; dc.DrawRoundedRectangle(LineWindow.Brush(i == 5 ? "#EFB071" : "#5D8292"), null, new Rect(40 + i * 75, 305 - h, 43, h), 5, 5); }
                }
            }
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); target.Render(visual); target.Freeze(); return target;
        }
        static void DrawText(DrawingContext dc, string text, int size, string color, int x, int y) {
            dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), size, LineWindow.Brush(color), 1.0), new Point(x, y));
        }
    }
}
