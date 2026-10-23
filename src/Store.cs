using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;

namespace ShaiTu {
    [DataContract] public sealed class Settings {
        [DataMember] public bool ClipboardEnabled = false;
        [DataMember] public int CapturePolicyVersion;
        [DataMember] public bool EdgeEnabled = true;
        [DataMember] public bool AutoHide = true;
        [DataMember] public int InteractionVersion;
        [DataMember] public bool HotkeyConfigured;
        [DataMember] public uint HotkeyModifiers = 3;
        [DataMember] public uint HotkeyKey = 84;
        [DataMember] public bool SnipasteEnabled = true;
        [DataMember] public bool SnipasteConfigured;
        [DataMember] public bool StartupEnabled = true;
        [DataMember] public bool StartupConfigured;
        [DataMember] public double HeightOffset;
        [DataMember] public string SnipasteFolder = "";
        [DataMember] public string WatchFolder = "";
        [DataMember] public List<string> Files = new List<string>();
        [DataMember] public List<PhotoPlacement> Placements = new List<PhotoPlacement>();
    }
    [DataContract] public sealed class PhotoPlacement {
        [DataMember] public string File;
        [DataMember] public double X;
    }
    public sealed class Photo {
        public string File;
        public string Hash;
        public BitmapSource Thumb;
        public int Width, Height;
        public long Length;
        public DateTime LastWriteUtc;
    }
    public sealed class Store {
        public const int Capacity = 8;
        public readonly string Root, Cache;
        public readonly List<Photo> Items = new List<Photo>();
        public Settings Settings;
        public event Action Changed;
        public Store(string root) {
            Root = root; Cache = Path.Combine(root, "images");
            Directory.CreateDirectory(Cache);
            Settings = ReadSettings(Path.Combine(Root, "settings.json")) ?? ReadSettings(Path.Combine(Root, "settings.json.bak")) ?? new Settings();
            if (Settings.Files == null) Settings.Files = new List<string>();
            if (Settings.Placements == null) Settings.Placements = new List<PhotoPlacement>();
            if (!Settings.SnipasteConfigured) { Settings.SnipasteEnabled = true; Settings.SnipasteConfigured = true; }
            if (!Settings.HotkeyConfigured || !HotkeyBinding.IsValid(Settings.HotkeyModifiers, Settings.HotkeyKey)) {
                Settings.HotkeyModifiers = 3; Settings.HotkeyKey = 84; Settings.HotkeyConfigured = true;
            }
            // One-time migration from the resident-display release; subsequent choices persist.
            bool migrateInteraction = Settings.InteractionVersion < 3;
            if (migrateInteraction) { Settings.AutoHide = true; Settings.EdgeEnabled = true; Settings.InteractionVersion = 3; }
            bool migrateCapture = Settings.CapturePolicyVersion < 1;
            if (migrateCapture) { Settings.ClipboardEnabled = false; Settings.CapturePolicyVersion = 1; }
            // Older builds only had the shortcut itself as the on/off state. Enable it
            // on first run; the welcome guide explains how to turn it off later.
            bool migrateStartup = !Settings.StartupConfigured;
            if (migrateStartup) { Settings.StartupEnabled = true; Settings.StartupConfigured = true; }
            if (Double.IsNaN(Settings.HeightOffset) || Double.IsInfinity(Settings.HeightOffset) || Settings.HeightOffset < 0) Settings.HeightOffset = 0;
            foreach (string name in Settings.Files.Take(Capacity)) {
                if (!IsOwnedName(name)) continue;
                string path = Path.Combine(Cache, name);
                try { Items.Add(LoadPhoto(path)); } catch (Exception e) { Log(e); }
            }
            // Only our GUID-named snapshots can be collected here.
            var live = new HashSet<string>(Items.Select(p => p.File), StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.GetFiles(Cache, "*.png"))
                if (IsOwnedName(Path.GetFileName(file)) && !live.Contains(file)) TryDelete(file);
            if ((migrateInteraction || migrateCapture || migrateStartup) && File.Exists(Path.Combine(Root, "settings.json"))) Save();
        }
        static Settings ReadSettings(string path) {
            try { using (var s = File.OpenRead(path)) return (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(s); }
            catch { return null; }
        }
        public static bool IsOwnedName(string name) {
            Guid value;
            return !String.IsNullOrEmpty(name) && Path.GetFileName(name) == name && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out value);
        }
        public void Save() {
            var state = new Settings { ClipboardEnabled = Settings.ClipboardEnabled, CapturePolicyVersion = Settings.CapturePolicyVersion, EdgeEnabled = Settings.EdgeEnabled,
                AutoHide = Settings.AutoHide, InteractionVersion = Settings.InteractionVersion, HotkeyConfigured = true,
                HotkeyModifiers = Settings.HotkeyModifiers, HotkeyKey = Settings.HotkeyKey,
                HeightOffset = Settings.HeightOffset, SnipasteEnabled = Settings.SnipasteEnabled, SnipasteConfigured = true,
                SnipasteFolder = Settings.SnipasteFolder, WatchFolder = Settings.WatchFolder, StartupEnabled = Settings.StartupEnabled, StartupConfigured = true,
                Files = Items.Select(p => Path.GetFileName(p.File)).ToList(), Placements = Settings.Placements ?? new List<PhotoPlacement>() };
            string path = Path.Combine(Root, "settings.json"), tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
                new DataContractJsonSerializer(typeof(Settings)).WriteObject(fs, state); fs.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true); else File.Move(tmp, path);
            Settings.Files = state.Files;
        }
        public bool ImportFile(string file, string origin = "手动添加") {
            var info = new FileInfo(file);
            if (info.Length > 25 * 1024 * 1024) throw new InvalidDataException("图片超过 25 MB，请先缩小。 ");
            return ImportImage(ReadImage(file, false), origin + ":" + Path.GetFileName(file));
        }
        public bool HasSnapshot(BitmapSource image) {
            string hash = ImageHash(image);
            return Items.Any(p => p.Hash == hash && File.Exists(p.File));
        }
        public bool ImportImage(BitmapSource image, string origin = "未指定") {
            if (image == null || image.PixelWidth < 1 || image.PixelHeight < 1) return false;
            if ((long)image.PixelWidth * image.PixelHeight > 32000000 || image.PixelWidth > 32768 || image.PixelHeight > 32768) throw new InvalidDataException("图片尺寸过大，请先缩小。 ");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            byte[] bytes;
            using (var ms = new MemoryStream()) { encoder.Save(ms); bytes = ms.ToArray(); }
            if (bytes.Length > 25 * 1024 * 1024) throw new InvalidDataException("PNG 图片超过 25 MB，请先缩小。 ");
            string hash = ImageHash(image);
            if (Items.Any(p => p.Hash == hash)) return false;
            string path = Path.Combine(Cache, Guid.NewGuid().ToString("N") + ".png");
            var previous = Items.ToArray();
            try {
                File.WriteAllBytes(path, bytes);
                Photo photo = LoadPhoto(path); photo.Hash = hash;
                Items.Insert(0, photo);
                while (Items.Count > Capacity) {
                    Photo removed = Items[Items.Count - 1];
                    Items.RemoveAt(Items.Count - 1);
                    ForgetPlacement(removed);
                }
                Save();
            } catch {
                Items.Clear(); Items.AddRange(previous); TryDelete(path); throw;
            }
            foreach (Photo p in previous) if (!Items.Contains(p)) TryDelete(p.File);
            LogCapture(origin, path, image.PixelWidth, image.PixelHeight);
            RaiseChanged(); return true;
        }
        public bool ReplaceImage(Photo photo, BitmapSource image) {
            if (photo == null || image == null) return false;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            byte[] bytes;
            using (var stream = new MemoryStream()) { encoder.Save(stream); bytes = stream.ToArray(); }
            if (bytes.Length > 25 * 1024 * 1024) throw new InvalidDataException("标注后的图片超过 25 MB，请缩小后再保存。");
            string temporary = photo.File + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            try {
                if (File.Exists(photo.File)) File.Replace(temporary, photo.File, null);
                else File.Move(temporary, photo.File);
            } catch {
                TryDelete(temporary);
                throw;
            }
            Photo loaded = LoadPhoto(photo.File);
            photo.Hash = loaded.Hash; photo.Thumb = loaded.Thumb;
            photo.Width = loaded.Width; photo.Height = loaded.Height;
            photo.Length = loaded.Length; photo.LastWriteUtc = loaded.LastWriteUtc;
            Save(); RaiseChanged();
            return true;
        }
        public void Remove(Photo p) {
            int index = Items.IndexOf(p); if (index < 0) return;
            Items.RemoveAt(index);
            ForgetPlacement(p);
            try { Save(); } catch { Items.Insert(index, p); throw; }
            TryDelete(p.File); RaiseChanged();
        }
        public double? PlacementFor(Photo photo) {
            if (photo == null || Settings.Placements == null) return null;
            string name = Path.GetFileName(photo.File);
            PhotoPlacement placement = Settings.Placements.FirstOrDefault(x => x != null && String.Equals(x.File, name, StringComparison.OrdinalIgnoreCase));
            return placement == null ? (double?)null : placement.X;
        }
        public void SetPlacement(Photo photo, double x) {
            if (photo == null) return;
            if (Settings.Placements == null) Settings.Placements = new List<PhotoPlacement>();
            string name = Path.GetFileName(photo.File);
            PhotoPlacement placement = Settings.Placements.FirstOrDefault(p => p != null && String.Equals(p.File, name, StringComparison.OrdinalIgnoreCase));
            if (placement == null) { placement = new PhotoPlacement { File = name }; Settings.Placements.Add(placement); }
            double old = placement.X;
            placement.X = Math.Max(0, Math.Min(1, x));
            try { Save(); } catch { placement.X = old; throw; }
        }
        void ForgetPlacement(Photo photo) {
            if (photo == null || Settings.Placements == null) return;
            string name = Path.GetFileName(photo.File);
            Settings.Placements.RemoveAll(p => p != null && String.Equals(p.File, name, StringComparison.OrdinalIgnoreCase));
        }
        public bool Swap(Photo source, Photo target) {
            int a = Items.IndexOf(source), b = Items.IndexOf(target);
            if (a < 0 || b < 0 || a == b) return false;
            Items[a] = target; Items[b] = source;
            try { Save(); } catch { Items[a] = source; Items[b] = target; throw; }
            RaiseChanged(); return true;
        }
        public void Clear() {
            Photo[] previous = Items.ToArray(); Items.Clear();
            if (Settings.Placements != null) Settings.Placements.Clear();
            try { Save(); } catch { Items.AddRange(previous); throw; }
            foreach (Photo p in previous) TryDelete(p.File); RaiseChanged();
        }
        public void ClearForExit() {
            Photo[] previous = Items.ToArray(); Items.Clear();
            if (Settings.Placements != null) Settings.Placements.Clear();
            try { Save(); } catch { Items.AddRange(previous); throw; }
            // Replace the backup too, so fallback settings cannot restore an old capture list.
            Save();
            var failures = new List<Exception>();
            foreach (string file in Directory.GetFiles(Cache, "*.png")) {
                if (!IsOwnedName(Path.GetFileName(file))) continue;
                try { File.Delete(file); } catch (Exception ex) { failures.Add(ex); }
            }
            RaiseChanged();
            TryDelete(Path.Combine(Root, "captures.log"));
            if (failures.Count > 0) throw new IOException("有截图缓存正在被其他程序占用，请关闭相关图片后再次退出。", failures[0]);
        }
        void LogCapture(string origin, string file, int width, int height) {
            try {
                string path = Path.Combine(Root, "captures.log");
                if (File.Exists(path) && new FileInfo(path).Length > 65536) File.WriteAllText(path, "");
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + origin.Replace('\r', ' ').Replace('\n', ' ') + " " + width + "x" + height + " -> " + Path.GetFileName(file) + Environment.NewLine);
            } catch { }
        }
        public void Refresh() {
            bool changed = false;
            foreach (Photo p in Items.ToArray()) {
                if (!File.Exists(p.File)) { Items.Remove(p); changed = true; continue; }
                var info = new FileInfo(p.File);
                if (info.Length == p.Length && info.LastWriteTimeUtc == p.LastWriteUtc) continue;
                Photo loaded = LoadPhoto(p.File);
                if (loaded.Hash != p.Hash) {
                    p.Hash = loaded.Hash; p.Thumb = loaded.Thumb;
                    p.Width = loaded.Width; p.Height = loaded.Height; changed = true;
                }
                p.Length = info.Length; p.LastWriteUtc = info.LastWriteTimeUtc;
            }
            if (changed) { Save(); RaiseChanged(); }
        }
        void RaiseChanged() { if (Changed != null) Changed(); }
        public void Log(Exception e) {
            try { File.AppendAllText(Path.Combine(Root, "errors.log"), DateTime.Now.ToString("s") + " " + e.GetType().Name + ": " + e.Message + Environment.NewLine); } catch { }
        }
        static void TryDelete(string path) { try { File.Delete(path); } catch { } }
        internal static string ImageHash(BitmapSource image) {
            BitmapSource bitmap = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            using (var sha = SHA256.Create()) {
                byte[] dimensions = BitConverter.GetBytes(bitmap.PixelWidth).Concat(BitConverter.GetBytes(bitmap.PixelHeight)).ToArray();
                sha.TransformBlock(dimensions, 0, dimensions.Length, dimensions, 0);
                int stride = checked(bitmap.PixelWidth * 4); byte[] row = new byte[stride];
                for (int y = 0; y < bitmap.PixelHeight; y++) {
                    bitmap.CopyPixels(new Int32Rect(0, y, bitmap.PixelWidth, 1), row, stride, 0);
                    sha.TransformBlock(row, 0, row.Length, row, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0); return Convert.ToBase64String(sha.Hash);
            }
        }
        public static BitmapSource ReadImage(string path, bool thumb) {
            // Metadata is read first so an oversized image is rejected before full decoding.
            int width, height;
            using (var stream = File.OpenRead(path)) {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                width = frame.PixelWidth; height = frame.PixelHeight;
                if ((long)frame.PixelWidth * frame.PixelHeight > 32000000 || width > 32768 || height > 32768) throw new InvalidDataException("图片尺寸过大。 ");
            }
            var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad;
            using (var stream = File.OpenRead(path)) {
                b.StreamSource = stream;
                if (thumb) {
                    if (width >= height) b.DecodePixelWidth = Math.Min(480, width);
                    else b.DecodePixelHeight = Math.Min(480, height);
                }
                b.EndInit(); b.Freeze(); return b;
            }
        }
        static Photo LoadPhoto(string path) {
            var info = new FileInfo(path);
            if (info.Length > 25 * 1024 * 1024) throw new InvalidDataException("图片超过 25 MB。 ");
            int width, height;
            using (var stream = File.OpenRead(path)) {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                width = decoder.Frames[0].PixelWidth; height = decoder.Frames[0].PixelHeight;
            }
            return new Photo { File = path, Hash = ImageHash(ReadImage(path, false)), Thumb = ReadImage(path, true), Width = width, Height = height,
                Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
        }
    }
}
