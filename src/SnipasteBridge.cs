using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Threading;

namespace ShaiTu {
    // Receive only final pin PNGs. SP1 records also exist for copy-only captures and contain
    // the pre-annotation selection, so they must never feed the automatic import path.
    public sealed class SnipasteBridge : IDisposable {
        readonly Action<BitmapSource> receive;
        readonly Action<Exception> error;
        readonly Dictionary<string, string> seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> attempts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> receivedPixels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root;
        int cycle;
        int dirty;
        FileSystemWatcher changes;
        public string HistoryFolder { get { return root; } }
        public event Action<string> CaptureAccepted;
        public event Action ScanRequested;
        public string CurrentFile { get; private set; }
        public SnipasteBridge(Action<BitmapSource> onImage, Action<Exception> onError) { receive = onImage; error = onError; }
        public static string Discover() {
            foreach (var process in Process.GetProcessesByName("Snipaste")) {
                using (process) {
                    try {
                        string folder = Path.Combine(Path.GetDirectoryName(process.MainModule.FileName), "history");
                        if (Directory.Exists(folder)) return folder;
                    } catch { }
                }
            }
            foreach (var special in new[] { Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData }) {
                string folder = Path.Combine(Environment.GetFolderPath(special), "Snipaste", "history");
                if (Directory.Exists(folder)) return folder;
            }
            return null;
        }
        public void Configure(string folder) {
            Dispose();
            root = String.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
            if (root != null && Directory.Exists(Path.Combine(root, "history"))) root = Path.Combine(root, "history");
            seen.Clear(); attempts.Clear(); receivedPixels.Clear();
            foreach (var info in Listing()) seen[info.FullName] = Stamp(info);
            if (root != null && Directory.Exists(root)) {
                changes = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler changed = delegate(object sender, FileSystemEventArgs e) {
                    string extension = Path.GetExtension(e.FullPath);
                    if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) return;
                    if (Interlocked.Exchange(ref dirty, 1) == 0 && ScanRequested != null) ScanRequested();
                };
                changes.Created += changed; changes.Changed += changed; changes.Renamed += delegate(object sender, RenamedEventArgs e) { changed(sender, e); };
                changes.Error += delegate { Interlocked.Exchange(ref dirty, 1); };
                changes.EnableRaisingEvents = true;
            }
        }
        public void Dispose() { if (changes != null) { changes.Dispose(); changes = null; } Interlocked.Exchange(ref dirty, 0); }
        IEnumerable<FileInfo> Listing() {
            if (root == null || !Directory.Exists(root)) return new FileInfo[0];
            return Directory.GetFiles(root, "*.png", SearchOption.AllDirectories)
                .Take(4096).Select(p => new FileInfo(p)).OrderBy(p => p.LastWriteTimeUtc).ToArray();
        }
        static string Stamp(FileInfo info) { return info.Length + ":" + info.LastWriteTimeUtc.Ticks; }
        public bool LatestAlreadyStored(Store store) {
            // Diagnostic comparison only; existing native pins and their files stay untouched.
            var latest = Listing().OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (latest == null) return false;
            try { return store.HasSnapshot(ReadCapture(latest.FullName)); } catch { return false; }
        }
        public void Tick() { Tick(false); }
        public void Tick(bool immediate) {
            if (!immediate && Interlocked.Exchange(ref dirty, 0) == 0 && ++cycle % 5 != 0) return;
            Interlocked.Exchange(ref dirty, 0);
            try {
                var files = Listing().ToArray(); var live = new HashSet<string>(files.Select(f => f.FullName), StringComparer.OrdinalIgnoreCase);
                foreach (string gone in seen.Keys.Where(k => !live.Contains(k)).ToArray()) { seen.Remove(gone); attempts.Remove(gone); receivedPixels.Remove(gone); }
                foreach (var info in files) {
                    string stamp = Stamp(info), old;
                    if (seen.TryGetValue(info.FullName, out old) && old == stamp) continue;
                    try {
                        if (info.Length == 0) continue;
                        CurrentFile = info.FullName;
                        var image = ReadCapture(info.FullName);
                        string hash = Store.ImageHash(image), previousHash;
                        // Native pins remain editable. Rewriting unchanged pixels must not rehang a removed snapshot.
                        if (!receivedPixels.TryGetValue(info.FullName, out previousHash) || previousHash != hash) {
                            receive(image);
                            receivedPixels[info.FullName] = hash;
                            if (CaptureAccepted != null) CaptureAccepted(info.FullName);
                        }
                        seen[info.FullName] = stamp; attempts.Remove(info.FullName);
                    } catch (Exception ex) {
                        int count; attempts.TryGetValue(info.FullName, out count); attempts[info.FullName] = count + 1;
                        if (count >= 9) { seen[info.FullName] = stamp; attempts.Remove(info.FullName); error(ex); }
                    } finally { CurrentFile = null; }
                }
            } catch (Exception ex) { error(ex); }
        }
        public static BitmapSource ReadCapture(string file) {
            if (new FileInfo(file).Length > 25 * 1024 * 1024) throw new InvalidDataException("Snipaste 图片超过 25 MB。");
            if (Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)) return Store.ReadImage(file, false);
            return DecodeHistory(File.ReadAllBytes(file));
        }
        public static BitmapSource DecodeHistory(byte[] data) {
            int end;
            byte[] png = ExtractPng(data, out end);
            // Qt's history record starts with the desktop backing image, not the selected output.
            // Version 201 stores its desktop origin (QPoint) and inclusive selection (QRect) after PNG.
            if (data.Length < 38 || ReadInt(data, 30) != 201 || ReadInt(data, 34) != 1)
                throw new InvalidDataException("不支持的 Snipaste 历史版本，请使用截图完成后的贴图接收。");
            if (end > data.Length - 24) throw new InvalidDataException("Snipaste 正在写入选区，请稍候。");
            long x = (long)ReadInt(data, end + 8) - ReadInt(data, end);
            long y = (long)ReadInt(data, end + 12) - ReadInt(data, end + 4);
            long width = (long)ReadInt(data, end + 16) - ReadInt(data, end + 8) + 1;
            long height = (long)ReadInt(data, end + 20) - ReadInt(data, end + 12) + 1;
            using (var ms = new MemoryStream(png)) {
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var f = decoder.Frames[0];
                if ((long)f.PixelWidth * f.PixelHeight > 32000000 || f.PixelWidth > 32768 || f.PixelHeight > 32768) throw new InvalidDataException("Snipaste 图片尺寸过大。");
                if (x < 0 || y < 0 || width < 1 || height < 1 || x + width > f.PixelWidth || y + height > f.PixelHeight)
                    throw new InvalidDataException("Snipaste 选区尚未完成或格式不受支持。");
                ms.Position = 0;
                var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = ms; image.EndInit(); image.Freeze();
                var selected = new CroppedBitmap(image, new Int32Rect((int)x, (int)y, (int)width, (int)height)); selected.Freeze(); return selected;
            }
        }
        static int ReadInt(byte[] bytes, int offset) { return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3]; }
        public static byte[] ExtractPng(byte[] data) {
            int end; return ExtractPng(data, out end);
        }
        static byte[] ExtractPng(byte[] data, out int endOffset) {
            endOffset = 0;
            if (data.Length < 46 || Encoding.BigEndianUnicode.GetString(data, 4, 26) != "SNIPASTE-SNIP")
                throw new InvalidDataException("不支持的 Snipaste 历史格式，可用剪贴板或自动保存文件夹接收。");
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            int start = -1;
            for (int i = 30; i <= Math.Min(512, data.Length - 8); i++) {
                bool matches = true; for (int j = 0; j < 8; j++) if (data[i + j] != signature[j]) { matches = false; break; }
                if (matches) { start = i; break; }
            }
            if (start < 0) throw new InvalidDataException("Snipaste 历史中没有 PNG 原图。");
            int offset = start + 8;
            while (offset <= data.Length - 12) {
                uint length = (uint)data[offset] << 24 | (uint)data[offset + 1] << 16 | (uint)data[offset + 2] << 8 | data[offset + 3];
                if (length > (uint)(data.Length - offset - 12)) break;
                bool end = Encoding.ASCII.GetString(data, offset + 4, 4) == "IEND";
                offset += (int)length + 12;
                if (end) { endOffset = offset; var result = new byte[offset - start]; Buffer.BlockCopy(data, start, result, 0, result.Length); return result; }
            }
            throw new InvalidDataException("Snipaste 正在写入图片，请稍候。");
        }
    }
}
