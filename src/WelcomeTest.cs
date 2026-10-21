using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ShaiTu {
    public static class WelcomeTest {
        public static int Run(string output) {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new WelcomeWindow(delegate { }, delegate { }) { Topmost = false, ShowInTaskbar = false, Left = 40, Top = 40 };
            window.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += delegate {
                timer.Stop();
                try {
                    window.UpdateLayout();
                    int width = (int)Math.Max(1, window.ActualWidth), height = (int)Math.Max(1, window.ActualHeight);
                    var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    target.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                    using (var stream = File.Create(Path.Combine(output, "welcome.png"))) encoder.Save(stream);
                    File.WriteAllText(Path.Combine(output, "welcome-results.txt"), "PASS: welcome rendered " + width + "x" + height);
                } catch (Exception ex) {
                    File.WriteAllText(Path.Combine(output, "welcome-results.txt"), "FAIL: " + ex);
                }
                window.Close(); app.Shutdown();
            };
            timer.Start();
            app.Run();
            return 0;
        }
    }
}
