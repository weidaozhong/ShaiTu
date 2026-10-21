using System;
using System.Windows.Media.Imaging;

namespace ShaiTu {
    // oneocr (Windows 11 Snipping Tool) when available, Windows OCR otherwise. Set
    // SHAITU_OCR=windows to compare against the built-in engine.
    public static class OcrService {
        static ITextRecognizer recognizer;
        static bool initialized;

        public static ITextRecognizer Recognizer {
            get {
                if (initialized) return recognizer;
                initialized = true;
                string forced = Environment.GetEnvironmentVariable("SHAITU_OCR");
                if (!String.Equals(forced, "windows", StringComparison.OrdinalIgnoreCase)) {
                    try {
                        var one = new OneOcrRecognizer();
                        if (one.IsAvailable) recognizer = one;
                    } catch { }
                }
                if (recognizer == null) recognizer = new WindowsOcrRecognizer();
                return recognizer;
            }
        }

        public static string Name { get { return Recognizer.Name; } }

        public static bool IsAvailable { get { return Recognizer != null && Recognizer.IsAvailable; } }

        public static OcrDocument Recognize(BitmapSource image) {
            ITextRecognizer current = Recognizer;
            try {
                return current.Recognize(image);
            } catch (Exception) {
                if (current is WindowsOcrRecognizer) throw;
                recognizer = new WindowsOcrRecognizer();
                return recognizer.Recognize(image);
            }
        }
    }
}
