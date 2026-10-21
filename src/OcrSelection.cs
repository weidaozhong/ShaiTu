using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace ShaiTu {
    // Pure selection logic over recognized words, in image-pixel coordinates.
    public static class OcrSelection {
        public static List<OcrWord> Words(OcrDocument document, Rect area) {
            var result = new List<OcrWord>();
            if (document == null) return result;
            foreach (OcrLine line in document.Lines) {
                foreach (OcrWord word in line.Words) {
                    if (area.IntersectsWith(word.Box)) result.Add(word);
                }
            }
            return result;
        }

        public static string Text(OcrDocument document, IList<OcrWord> selection) {
            if (document == null || selection == null || selection.Count == 0) return "";
            var selected = new HashSet<OcrWord>(selection);
            var builder = new StringBuilder();
            foreach (OcrLine line in document.Lines) {
                var words = new List<OcrWord>();
                foreach (OcrWord word in line.Words) if (selected.Contains(word)) words.Add(word);
                if (words.Count == 0) continue;
                if (builder.Length > 0) builder.Append("\r\n");
                builder.Append(OcrText.JoinWords(words));
            }
            return OcrText.NormalizePunctuation(builder.ToString());
        }

        public static OcrWord WordAt(OcrDocument document, Point point) {
            if (document == null) return null;
            foreach (OcrLine line in document.Lines) {
                foreach (OcrWord word in line.Words) {
                    if (word.Box.Contains(point)) return word;
                }
            }
            return null;
        }

        public static OcrLine LineAt(OcrDocument document, Point point) {
            if (document == null) return null;
            foreach (OcrLine line in document.Lines) {
                if (line.Box.Contains(point)) return line;
            }
            return null;
        }

        public static List<OcrWord> All(OcrDocument document) {
            var result = new List<OcrWord>();
            if (document == null) return result;
            foreach (OcrLine line in document.Lines) result.AddRange(line.Words);
            return result;
        }
    }
}
