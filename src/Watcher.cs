using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;

namespace ShaiTu {
    public sealed class IncomingWatcher : IDisposable {
        readonly Dispatcher dispatcher;
        readonly Action<string> receive;
        readonly Action<Exception> error;
        readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HashSet<string>> known = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        string[] folders = new string[0];
        int cycle;
        bool disposed;
        DateTime configuredUtc;
        sealed class Pending { public int Attempts; public long Size = -1; }
        static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
        public static bool IsImage(string path) { return Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()); }
        public IncomingWatcher(Dispatcher ui, Action<string> onFile, Action<Exception> onError) { dispatcher = ui; receive = onFile; error = onError; }
        public void Configure(string[] paths) {
            foreach (var w in watchers) w.Dispose(); watchers.Clear(); pending.Clear(); known.Clear();
            configuredUtc = DateTime.UtcNow;
            folders = paths.Where(p => !String.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string folder in folders) Attach(folder, false);
        }
        void Attach(string folder, bool appearedLater) {
            if (!Directory.Exists(folder) || known.ContainsKey(folder)) return;
            try {
                // Enable events before the baseline scan to avoid a capture being lost during setup.
                var watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 16384 };
                watcher.Created += delegate(object s, FileSystemEventArgs e) { Queue(e.FullPath); };
                watcher.Renamed += delegate(object s, RenamedEventArgs e) { Queue(e.FullPath); };
                watcher.Changed += delegate(object s, FileSystemEventArgs e) { dispatcher.BeginInvoke(new Action(delegate { if (pending.ContainsKey(e.FullPath)) pending[e.FullPath].Size = -1; })); };
                watcher.Error += delegate { dispatcher.BeginInvoke(new Action(delegate { Scan(folder); })); };
                watcher.EnableRaisingEvents = true;
                known[folder] = new HashSet<string>(Directory.GetFiles(folder).Where(IsImage), StringComparer.OrdinalIgnoreCase);
                if (appearedLater) foreach (string file in known[folder]) if (File.GetCreationTimeUtc(file) >= configuredUtc) Queue(file);
                watchers.Add(watcher);
            } catch (Exception ex) { error(ex); }
        }
        void Queue(string path) {
            if (!IsImage(path) || disposed) return;
            dispatcher.BeginInvoke(new Action(delegate {
                if (!disposed && pending.Count < 128 && !pending.ContainsKey(path)) pending[path] = new Pending();
            }));
        }
        void Scan(string folder) {
            try {
                if (!Directory.Exists(folder)) return;
                if (!known.ContainsKey(folder)) { Attach(folder, true); return; }
                var current = new HashSet<string>(Directory.GetFiles(folder).Where(IsImage), StringComparer.OrdinalIgnoreCase);
                foreach (string file in current) if (!known[folder].Contains(file)) Queue(file);
                known[folder] = current;
            } catch (Exception ex) { error(ex); }
        }
        public void Tick() {
            if (disposed) return;
            cycle++;
            if (cycle % 5 == 0) foreach (string folder in folders) if (!known.ContainsKey(folder)) Attach(folder, true);
            if (cycle % 150 == 0) foreach (string folder in folders) Scan(folder);
            foreach (var entry in pending.ToArray()) {
                var p = entry.Value; p.Attempts++;
                try {
                    if (!File.Exists(entry.Key)) { if (p.Attempts > 15) pending.Remove(entry.Key); continue; }
                    long size = new FileInfo(entry.Key).Length;
                    if (size == 0 || size != p.Size) { p.Size = size; if (p.Attempts > 50) pending.Remove(entry.Key); continue; }
                    receive(entry.Key); pending.Remove(entry.Key);
                    string folder = Path.GetDirectoryName(entry.Key);
                    if (known.ContainsKey(folder)) known[folder].Add(entry.Key);
                } catch (Exception ex) { if (p.Attempts >= 25) { pending.Remove(entry.Key); error(ex); } }
            }
        }
        public void Dispose() { disposed = true; foreach (var w in watchers) w.Dispose(); watchers.Clear(); pending.Clear(); }
    }
}
