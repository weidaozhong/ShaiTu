using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinLanguage = Windows.Globalization.Language;
using WinOcrLine = Windows.Media.Ocr.OcrLine;
using WinOcrWord = Windows.Media.Ocr.OcrWord;
using Rect = System.Windows.Rect;

namespace ShaiTu {
    public sealed class OcrWord {
        public string Text;
        public Rect Box;
    }

    public sealed class OcrLine {
        public readonly List<OcrWord> Words = new List<OcrWord>();
        public Rect Box;
        public string RawText;
        public string Text { get { return String.IsNullOrEmpty(RawText) ? OcrText.JoinWords(Words) : RawText; } }
    }

    public sealed class OcrDocument {
        public readonly List<OcrLine> Lines = new List<OcrLine>();
        public string Language = "";
        public int PixelWidth, PixelHeight;
        public string Text {
            get {
                var builder = new StringBuilder();
                foreach (OcrLine line in Lines) {
                    if (line.Words.Count == 0) continue;
                    if (builder.Length > 0) builder.Append("\r\n");
                    builder.Append(line.Text);
                }
                return builder.ToString();
            }
        }
        public int WordCount {
            get { int count = 0; foreach (OcrLine line in Lines) count += line.Words.Count; return count; }
        }
    }

    public interface ITextRecognizer {
        string Name { get; }
        bool IsAvailable { get; }
        OcrDocument Recognize(BitmapSource image);
    }

    // Windows 10/11 自带的 WinRT OCR。零体积、离线、有词框，适合做文字层。
    public sealed class WindowsOcrRecognizer : ITextRecognizer, IDisposable {
        OcrEngine engine;
        bool tried;

        public string Name { get { return "Windows OCR"; } }

        public bool IsAvailable { get { return Engine != null; } }

        OcrEngine Engine {
            get {
                if (!tried) { tried = true; engine = CreateEngine(); }
                return engine;
            }
        }

        static OcrEngine CreateEngine() {
            try {
                var languages = OcrEngine.AvailableRecognizerLanguages;
                // 中文优先，其次用户配置，最后英文；三者都不可用时返回 null。
                WinLanguage preferred = Find(languages, "zh-Hans");
                if (preferred == null) preferred = Find(languages, "zh-Hant");
                if (preferred != null) {
                    OcrEngine localized = OcrEngine.TryCreateFromLanguage(preferred);
                    if (localized != null) return localized;
                }
                OcrEngine profile = OcrEngine.TryCreateFromUserProfileLanguages();
                if (profile != null) return profile;
                WinLanguage fallback = Find(languages, "en-US");
                return fallback == null ? null : OcrEngine.TryCreateFromLanguage(fallback);
            } catch { return null; }
        }

        static WinLanguage Find(IReadOnlyList<WinLanguage> languages, string prefix) {
            if (languages == null) return null;
            foreach (WinLanguage language in languages) {
                if (language.LanguageTag != null && language.LanguageTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return language;
            }
            return null;
        }

        public OcrDocument Recognize(BitmapSource image) {
            OcrEngine ocr = Engine;
            if (ocr == null) throw new InvalidOperationException("系统没有可用的文字识别语言包。请在 Windows 设置 → 应用 → 可选功能中安装中文 OCR。");
            if (image == null || image.PixelWidth < 1 || image.PixelHeight < 1) throw new ArgumentException("图片为空。");
            double scale;
            using (SoftwareBitmap software = ToSoftwareBitmap(image, out scale)) {
                OcrResult result = Await(ocr.RecognizeAsync(software));
                var document = new OcrDocument { PixelWidth = image.PixelWidth, PixelHeight = image.PixelHeight, Language = ocr.RecognizerLanguage == null ? "" : ocr.RecognizerLanguage.LanguageTag };
                foreach (WinOcrLine sourceLine in result.Lines) {
                    var line = new OcrLine();
                    Rect union = Rect.Empty;
                    foreach (WinOcrWord sourceWord in sourceLine.Words) {
                        Windows.Foundation.Rect bounds = sourceWord.BoundingRect;
                        var word = new OcrWord {
                            Text = sourceWord.Text,
                            Box = new Rect(bounds.X / scale, bounds.Y / scale, bounds.Width / scale, bounds.Height / scale)
                        };
                        line.Words.Add(word);
                        union = union.IsEmpty ? word.Box : Rect.Union(union, word.Box);
                    }
                    if (line.Words.Count == 0) continue;
                    line.Box = union;
                    document.Lines.Add(line);
                }
                return document;
            }
        }

        static SoftwareBitmap ToSoftwareBitmap(BitmapSource source, out double scale) {
            int limit = (int)OcrEngine.MaxImageDimension;
            scale = 1.0;
            if (limit > 0 && (source.PixelWidth > limit || source.PixelHeight > limit)) {
                scale = Math.Min((double)limit / source.PixelWidth, (double)limit / source.PixelHeight);
                if (scale <= 0) scale = 1.0;
            }
            BitmapSource scaled = source;
            if (scale < 1.0) {
                var transform = new TransformedBitmap(source, new ScaleTransform(scale, scale));
                transform.Freeze();
                scaled = transform;
            }
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            int width = converted.PixelWidth, height = converted.PixelHeight, stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);
            var writer = new DataWriter();
            writer.WriteBytes(pixels);
            IBuffer buffer = writer.DetachBuffer();
            writer.Dispose();
            return SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        }

        static T Await<T>(IAsyncOperation<T> operation) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (operation.Status == AsyncStatus.Started) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("文字识别超时，请换一张小一点的图片。");
                Thread.Sleep(10);
            }
            if (operation.Status != AsyncStatus.Completed) throw new InvalidOperationException("文字识别失败：" + operation.Status);
            return operation.GetResults();
        }

        public void Dispose() { engine = null; tried = false; }
    }

    // 中文按字返回时 WinRT 会在每个字之间插入空格；这里按 Unicode 类别重新拼接。
    public static class OcrText {
        static readonly char[] CjkTight = new[] {
            '\u3001', '\u3002', '\u30FB', '\uFF01', '\uFF05', '\uFF0C', '\uFF0E',
            '\uFF1A', '\uFF1B', '\uFF1F', '\uFF61', '\uFF64', '\uFF65'
        };

        public static string JoinWords(IList<OcrWord> words) {
            if (words == null || words.Count == 0) return "";
            var builder = new StringBuilder();
            string previous = null;
            foreach (OcrWord word in words) {
                if (String.IsNullOrEmpty(word.Text)) continue;
                if (previous != null && NeedsSpace(previous, word.Text)) builder.Append(' ');
                builder.Append(word.Text);
                previous = word.Text;
            }
            return builder.ToString();
        }

        static bool NeedsSpace(string left, string right) {
            if (left.Length == 0 || right.Length == 0) return false;
            int leftCode = LastCodePoint(left), rightCode = FirstCodePoint(right);
            if (leftCode < 0 || rightCode < 0) return false;
            UnicodeCategory leftCategory = CategoryOf(leftCode);
            UnicodeCategory rightCategory = CategoryOf(rightCode);
            if (IsOpening(leftCategory) || IsClosing(rightCategory)) return false;
            if (IsPunctuationOnly(right) && IsSuffix(rightCode)) return false;
            if (IsCjkTight(leftCode) || IsCjkTight(rightCode)) return false;
            if (IsCombining(rightCategory)) return false;
            return UsesSpaces(left) || UsesSpaces(right);
        }

        static bool UsesSpaces(string text) {
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (Char.IsHighSurrogate(c) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1])) { i++; continue; }
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.UppercaseLetter || category == UnicodeCategory.LowercaseLetter
                    || category == UnicodeCategory.TitlecaseLetter || category == UnicodeCategory.DecimalDigitNumber) return true;
            }
            return false;
        }

        static bool IsPunctuationOnly(string text) {
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (Char.IsHighSurrogate(c) && i + 1 < text.Length && Char.IsLowSurrogate(text[i + 1])) { i++; continue; }
                if (!Char.IsPunctuation(c)) return false;
            }
            return text.Length > 0;
        }

        static bool IsOpening(UnicodeCategory category) {
            return category == UnicodeCategory.OpenPunctuation || category == UnicodeCategory.InitialQuotePunctuation;
        }

        static bool IsClosing(UnicodeCategory category) {
            return category == UnicodeCategory.ClosePunctuation || category == UnicodeCategory.FinalQuotePunctuation;
        }

        static bool IsCombining(UnicodeCategory category) {
            return category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark || category == UnicodeCategory.EnclosingMark;
        }

        static UnicodeCategory CategoryOf(int code) {
            if (code <= 0xFFFF) return CharUnicodeInfo.GetUnicodeCategory((char)code);
            return CharUnicodeInfo.GetUnicodeCategory(Char.ConvertFromUtf32(code), 0);
        }

        static bool IsSuffix(int code) {
            return code == ',' || code == '.' || code == ':' || code == ';' || code == '!' || code == '?' || code == '%'
                || code == 0x2025 || code == 0x2026;
        }

        static bool IsCjkTight(int code) {
            for (int i = 0; i < CjkTight.Length; i++) if (CjkTight[i] == code) return true;
            return false;
        }

        static int FirstCodePoint(string text) {
            if (text.Length == 0) return -1;
            if (Char.IsHighSurrogate(text[0]) && text.Length > 1 && Char.IsLowSurrogate(text[1])) return Char.ConvertToUtf32(text[0], text[1]);
            return text[0];
        }

        static int LastCodePoint(string text) {
            if (text.Length == 0) return -1;
            int last = text.Length - 1;
            if (Char.IsLowSurrogate(text[last]) && last > 0 && Char.IsHighSurrogate(text[last - 1])) return Char.ConvertToUtf32(text[last - 1], text[last]);
            return text[last];
        }

        // The Snipping Tool engine returns ASCII punctuation even for Chinese text.
        public static string NormalizePunctuation(string text) {
            if (String.IsNullOrEmpty(text)) return text;
            var builder = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                char previous = i > 0 ? text[i - 1] : '\0';
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (IsCjkChar(previous) || IsCjkChar(next)) {
                    switch (c) {
                        case ',': c = '，'; break;
                        case ':': c = '：'; break;
                        case ';': c = '；'; break;
                        case '!': c = '！'; break;
                        case '?': c = '？'; break;
                        case '(': c = '（'; break;
                        case ')': c = '）'; break;
                    }
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        static bool IsCjkChar(char c) {
            return (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x3000 && c <= 0x303F);
        }

        // PP-OCR often inserts a space token between CJK characters; keep Latin spaces,
        // drop spaces next to Chinese characters and CJK punctuation.
        public static string Tighten(string text) {
            if (String.IsNullOrEmpty(text)) return text;
            var builder = new StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char c in text) {
                if (c == ' ' || c == '\t') { if (builder.Length > 0) pendingSpace = true; continue; }
                if (pendingSpace) {
                    char previous = builder[builder.Length - 1];
                    if (!IsCjkOrPunct(previous) && !IsCjkOrPunct(c)) builder.Append(' ');
                    pendingSpace = false;
                }
                builder.Append(c);
            }
            return builder.ToString().Trim();
        }

        static bool IsCjkOrPunct(char c) {
            return (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF)
                || (c >= 0x3000 && c <= 0x303F) || (c >= 0xFF00 && c <= 0xFFEF)
                || c == '\u00B7' || c == '\u2014' || c == '\u201C' || c == '\u201D' || c == '\u2018' || c == '\u2019';
        }

        public static string CollapseSpaces(string text) {
            if (String.IsNullOrEmpty(text)) return "";
            var builder = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text) {
                if (c == ' ' || c == '\t') { space = builder.Length > 0; continue; }
                if (space && builder.Length > 0 && c != '\r' && c != '\n') builder.Append(' ');
                space = false;
                builder.Append(c);
            }
            return builder.ToString();
        }
    }
}
