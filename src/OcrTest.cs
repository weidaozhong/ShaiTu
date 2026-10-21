using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShaiTu {
    // Headless OCR check: renders a fixed Chinese/English sample, recognizes it, and
    // records both the raw lines and the formatted text for manual inspection.
    public static class OcrTest {
        public static int Run(string output) {
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);
            var report = new List<string>();
            try {
                var recognizer = OcrService.Recognizer;
                report.Add("Engine: " + recognizer.Name + ", available: " + recognizer.IsAvailable);
                if (!recognizer.IsAvailable) {
                    report.Add("SKIP: no OCR language pack installed");
                    File.WriteAllLines(Path.Combine(output, "ocr-results.txt"), report.ToArray());
                    return 0;
                }
                BitmapSource image = DrawSample();
                WritePng(image, Path.Combine(output, "ocr-sample.png"));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                OcrDocument document = recognizer.Recognize(image);
                watch.Stop();
                report.Add("Image: " + image.PixelWidth + " x " + image.PixelHeight);
                report.Add("Elapsed: " + watch.ElapsedMilliseconds + " ms");
                report.Add("Language: " + document.Language);
                report.Add("Lines: " + document.Lines.Count + ", words: " + document.WordCount);
                report.Add("FORMATTED>>>" + document.Text.Replace("\r\n", " / ") + "<<<");
                foreach (OcrLine line in document.Lines) {
                    report.Add("line [" + (int)line.Box.X + "," + (int)line.Box.Y + " " + (int)line.Box.Width + "x" + (int)line.Box.Height + "]: " + line.Text);
                }
                string text = document.Text;
                bool ok = text.Contains("设置") && text.Contains("网络") && text.Contains("存储")
                    && text.Contains("立即同步") && text.Contains("Storage settings") && text.Contains("工作");
                report.Add(ok ? "PASS: OCR sample" : "FAIL: OCR sample");
                File.WriteAllLines(Path.Combine(output, "ocr-results.txt"), report.ToArray());
                return ok ? 0 : 1;
            } catch (Exception ex) {
                report.Add("ERROR: " + ex);
                File.WriteAllLines(Path.Combine(output, "ocr-results.txt"), report.ToArray());
                return 1;
            }
        }

        static BitmapSource DrawSample() {
            int width = 1800, height = 840;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen()) {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                DrawText(dc, "设置 · 网络与存储", 40, "#202020", 80, 80, true);
                DrawText(dc, "同步文件夹：D:\\工作\\截图", 26, "#606060", 80, 220, false);
                DrawText(dc, "自动同步已关闭，点击“立即同步”重试。", 26, "#606060", 80, 300, false);
                DrawText(dc, "Storage  settings  and  sync  folder", 26, "#606060", 80, 400, false);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 120, 215)), null, new Rect(80, 540, 300, 88), 8, 8);
                DrawText(dc, "立即同步", 30, "#FFFFFF", 128, 556, true);
            }
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.Freeze();
            return target;
        }

        static void DrawText(DrawingContext dc, string text, int size, string color, int x, int y, bool bold) {
            var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                typeface, size, LineWindow.Brush(color), 1.0);
            dc.DrawText(formatted, new Point(x, y));
        }

        static void WritePng(BitmapSource bitmap, string path) {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
