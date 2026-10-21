using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace ShaiTu {
    // Batch OCR over a folder, for comparing engines and measuring real screenshots.
    public static class OcrBatch {
        public static int Run(string folder) {
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            string resultPath = Path.Combine(folder, "ocr-batch.txt");
            var report = new List<string>();
            var files = new List<string>();
            foreach (string pattern in new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" }) files.AddRange(Directory.GetFiles(folder, pattern));
            files.Sort(StringComparer.OrdinalIgnoreCase);
            report.Add("Folder: " + folder);
            report.Add("Files: " + files.Count);
            if (files.Count == 0) {
                report.Add("Drop 10-20 representative screenshots here, then run --ocr-batch again.");
                File.WriteAllLines(resultPath, report.ToArray());
                return 0;
            }
            var recognizer = OcrService.Recognizer;
            report.Add("Engine: " + recognizer.Name + ", available: " + recognizer.IsAvailable);
            long totalMs = 0; int characters = 0; int compared = 0; double cerTotal = 0;
            foreach (string file in files) {
                report.Add("");
                report.Add("=== " + Path.GetFileName(file) + " ===");
                try {
                    BitmapSource image = Store.ReadImage(file, false);
                    var watch = Stopwatch.StartNew();
                    OcrDocument document = recognizer.Recognize(image);
                    watch.Stop();
                    totalMs += watch.ElapsedMilliseconds; characters += document.Text.Length;
                    report.Add("size=" + image.PixelWidth + "x" + image.PixelHeight + " ms=" + watch.ElapsedMilliseconds + " words=" + document.WordCount + " lines=" + document.Lines.Count);
                    report.Add(document.Text);
                    string truthPath = Path.ChangeExtension(file, ".txt");
                    if (File.Exists(truthPath)) {
                        string truth = File.ReadAllText(truthPath);
                        double cer = ErrorRate(truth, document.Text);
                        report.Add("CER (content, punctuation ignored): " + (cer * 100).ToString("F1") + "%");
                        cerTotal += cer; compared++;
                    }
                } catch (Exception ex) {
                    report.Add("ERROR: " + ex.Message);
                }
            }
            report.Add("");
            report.Add("Total: " + files.Count + " image(s), " + totalMs + " ms, " + characters + " characters");
            if (compared > 0) report.Add("Average CER: " + (cerTotal / compared * 100).ToString("F1") + "% over " + compared + " image(s) with .txt ground truth");
            File.WriteAllLines(resultPath, report.ToArray(), new UTF8Encoding(false));
            return 0;
        }

        static double ErrorRate(string reference, string hypothesis) {
            string a = Normalize(reference), b = Normalize(hypothesis);
            if (a.Length == 0) return b.Length == 0 ? 0 : 1;
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) previous[j] = j;
            for (int i = 1; i <= a.Length; i++) {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++) {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                }
                var swap = previous; previous = current; current = swap;
            }
            return (double)previous[b.Length] / a.Length;
        }

        static string Normalize(string text) {
            if (text == null) return "";
            string normalized = text.Normalize(NormalizationForm.FormKC);
            var builder = new StringBuilder(normalized.Length);
            foreach (char c in normalized) {
                if (Char.IsWhiteSpace(c)) continue;
                if (Char.IsPunctuation(c) || Char.IsSymbol(c)) continue;
                builder.Append(c);
            }
            return builder.ToString();
        }
    }
}
