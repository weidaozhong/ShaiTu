using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ShaiTu {
    // Renders the workbench off-screen-ish, waits for OCR, and saves a PNG so the
    // layout and the text layer can be inspected without a human at the keyboard.
    public static class WorkbenchTest {
        public static int Run(string output) {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            var report = new System.Collections.Generic.List<string>();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int exit = 0;
            try {
                var store = new Store(Path.Combine(output, "state"));
                store.ImportImage(SelfTest.DrawSample(0), "workbench-test");
                var window = new WorkbenchWindow(store, store.Items[0], delegate(Exception ex) { report.Add("ERROR: " + ex.Message); }, delegate { });
                window.Topmost = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = 40; window.Top = 40;
                window.Show();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                int ticks = 0;
                timer.Tick += delegate {
                    ticks++;
                    if (!window.HasText && ticks < 60) return;
                    timer.Stop();
                    try {
                        Render(window, Path.Combine(output, "workbench-text.png"));
                        report.Add("Window: " + (int)window.ActualWidth + " x " + (int)window.ActualHeight);
                        report.Add("OCR ready: " + window.HasText);
                        report.Add("Words: " + window.WordCount + ", scale: " + window.DisplayScale);
                        report.Add("TEXT>>>" + window.RecognizedText.Replace("\r\n", " / ") + "<<<");
                        window.SelectAll();
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Render);
                        Render(window, Path.Combine(output, "workbench-selected.png"));
                        report.Add("Select-all rendered");
                        var other = new Window { Width = 220, Height = 120, WindowStyle = WindowStyle.None, Topmost = true, ShowInTaskbar = false, Background = Brushes.White };
                        other.Show(); other.Activate();
                        window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Background);
                        bool minimized = window.WindowState == WindowState.Minimized;
                        window.Restore();
                        window.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Background);
                        bool restored = window.WindowState == WindowState.Normal;
                        report.Add("Outside click minimizes: " + minimized + ", restores: " + restored);
                        other.Close();
                        exit = window.HasText && minimized && restored ? 0 : 1;
                    } catch (Exception ex) { report.Add("ERROR: " + ex); exit = 1; }
                    File.WriteAllLines(Path.Combine(output, "workbench-results.txt"), report.ToArray());
                    window.Close();
                    app.Shutdown(exit);
                };
                timer.Start();
                app.Run();
            } catch (Exception ex) {
                report.Add("ERROR: " + ex);
                File.WriteAllLines(Path.Combine(output, "workbench-results.txt"), report.ToArray());
                return 1;
            }
            return exit;
        }

        static void Render(Window window, string path) {
            window.UpdateLayout();
            int width = (int)Math.Max(1, window.ActualWidth), height = (int)Math.Max(1, window.ActualHeight);
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
