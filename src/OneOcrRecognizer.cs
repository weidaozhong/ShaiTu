using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShaiTu {
    // Windows 11 Snipping Tool's OCR engine (oneocr.dll). Microsoft does not allow the
    // DLL to load from WindowsApps, so the three required files are copied into the
    // app's LocalAppData cache on first use and loaded from there. Nothing is shipped.
    public sealed class OneOcrRecognizer : ITextRecognizer, IDisposable {
        const string PackageFamily = "Microsoft.ScreenSketch_8wekyb3d8bbwe";
        const string ModelKey = "kj)TGtrK>f]b[Piow.gU+nC@s\"\"\"\"\"\"4";
        const string DllName = "oneocr.dll";
        const string ModelName = "oneocr.onemodel";
        const string RuntimeName = "onnxruntime.dll";
        const int MinSide = 50;

        readonly object gate = new object();
        IntPtr module, scratch;
        long initOptions, pipeline, processOptions;
        Api api;
        string cacheFolder;

        public string Name { get { return "oneocr"; } }
        public bool IsAvailable { get; private set; }
        public string Problem { get; private set; }
        public string Folder { get { return cacheFolder; } }

        public OneOcrRecognizer() {
            try { Load(); IsAvailable = true; }
            catch (Exception ex) { Problem = ex.Message; IsAvailable = false; Close(); }
        }

        void Load() {
            string overrideDirectory = Environment.GetEnvironmentVariable("SHAITU_ONEOCR_DIR");
            if (!String.IsNullOrEmpty(overrideDirectory) && HasFiles(overrideDirectory)) cacheFolder = overrideDirectory;
            else {
                string source, packageFullName;
                if (!FindPackage(out packageFullName, out source)) throw new InvalidOperationException("未找到 Windows 11 截图工具（Snipping Tool）的 OCR 组件。");
                cacheFolder = PrepareFiles(packageFullName, source);
            }
            scratch = Marshal.AllocHGlobal(8);
            SetDllDirectory(cacheFolder);
            try {
                // LOAD_WITH_ALTERED_SEARCH_PATH lets oneocr.dll find onnxruntime.dll.
                module = LoadLibraryEx(Path.Combine(cacheFolder, DllName), IntPtr.Zero, 8);
                if (module == IntPtr.Zero) throw new InvalidOperationException("oneocr.dll 加载失败，错误码 " + Marshal.GetLastWin32Error() + "。");
                api = new Api();
                api.CreateInit = Bind<CreateOcrInitOptions>("CreateOcrInitOptions");
                api.SetDelayLoad = Bind<OcrInitOptionsSetUseModelDelayLoad>("OcrInitOptionsSetUseModelDelayLoad");
                api.CreatePipeline = Bind<CreateOcrPipeline>("CreateOcrPipeline");
                api.CreateProcessOptions = Bind<CreateOcrProcessOptions>("CreateOcrProcessOptions");
                api.SetMaxLines = Bind<OcrProcessOptionsSetMaxRecognitionLineCount>("OcrProcessOptionsSetMaxRecognitionLineCount");
                api.Run = Bind<RunOcrPipeline>("RunOcrPipeline");
                api.LineCount = Bind<GetOcrLineCount>("GetOcrLineCount");
                api.Line = Bind<GetOcrLine>("GetOcrLine");
                api.LineContent = Bind<GetOcrLineContent>("GetOcrLineContent");
                api.LineBox = Bind<GetOcrLineBoundingBox>("GetOcrLineBoundingBox");
                api.LineWordCount = Bind<GetOcrLineWordCount>("GetOcrLineWordCount");
                api.Word = Bind<GetOcrWord>("GetOcrWord");
                api.WordContent = Bind<GetOcrWordContent>("GetOcrWordContent");
                api.WordBox = Bind<GetOcrWordBoundingBox>("GetOcrWordBoundingBox");
                api.Release = Bind<Release>("ReleaseOcrResult");
                api.ReleaseInit = Bind<Release>("ReleaseOcrInitOptions");
                api.ReleasePipeline = Bind<Release>("ReleaseOcrPipeline");
                api.ReleaseProcess = Bind<Release>("ReleaseOcrProcessOptions");

                Check(api.CreateInit(scratch), "初始化 OCR 失败");
                initOptions = Marshal.ReadInt64(scratch);
                Check(api.SetDelayLoad(initOptions, 0), "配置 OCR 失败");
                IntPtr model = Utf8(Path.Combine(cacheFolder, ModelName));
                IntPtr key = Utf8(ModelKey);
                try {
                    Check(api.CreatePipeline(model, key, initOptions, scratch), "创建 OCR 引擎失败");
                    pipeline = Marshal.ReadInt64(scratch);
                } finally {
                    Marshal.FreeHGlobal(model); Marshal.FreeHGlobal(key);
                }
                Check(api.CreateProcessOptions(scratch), "创建 OCR 参数失败");
                processOptions = Marshal.ReadInt64(scratch);
                Check(api.SetMaxLines(processOptions, 1000), "配置 OCR 行数失败");
            } finally { SetDllDirectory(null); }
        }

        public OcrDocument Recognize(BitmapSource image) {
            if (!IsAvailable) throw new InvalidOperationException("oneocr 不可用：" + Problem);
            if (image == null) throw new ArgumentException("图片为空。");
            int width, height;
            byte[] pixels = ToBgra(image, out width, out height);
            int originalWidth = width, originalHeight = height;
            if (width < MinSide || height < MinSide) {
                int paddedWidth = Math.Max(width, MinSide), paddedHeight = Math.Max(height, MinSide);
                var padded = new byte[paddedWidth * paddedHeight * 4];
                for (int i = 0; i < padded.Length; i += 4) { padded[i] = 255; padded[i + 1] = 255; padded[i + 2] = 255; padded[i + 3] = 255; }
                for (int y = 0; y < height; y++) Buffer.BlockCopy(pixels, y * width * 4, padded, y * paddedWidth * 4, width * 4);
                pixels = padded; width = paddedWidth; height = paddedHeight;
            }
            lock (gate) {
                var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                long result = 0;
                try {
                    var native = new OcrImage { Type = 3, Width = width, Height = height, Reserved = 0, Step = width * 4, Data = handle.AddrOfPinnedObject() };
                    Check(api.Run(pipeline, ref native, processOptions, scratch), "文字识别失败");
                    result = Marshal.ReadInt64(scratch);
                    var document = new OcrDocument { PixelWidth = originalWidth, PixelHeight = originalHeight, Language = "zh-Hans" };
                    if (api.LineCount(result, scratch) != 0) return document;
                    long lineCount = Marshal.ReadInt64(scratch);
                    for (long i = 0; i < lineCount; i++) {
                        if (api.Line(result, i, scratch) != 0) continue;
                        long line = Marshal.ReadInt64(scratch);
                        if (line == 0) continue;
                        if (api.LineContent(line, scratch) != 0) continue;
                        string text = OcrText.NormalizePunctuation(Utf8(Marshal.ReadIntPtr(scratch)));
                        Rect lineBox = ReadBox(line, true);
                        var words = new List<OcrWord>();
                        if (api.LineWordCount(line, scratch) == 0) {
                            long wordCount = Marshal.ReadInt64(scratch);
                            for (long w = 0; w < wordCount; w++) {
                                if (api.Word(line, w, scratch) != 0) continue;
                                long word = Marshal.ReadInt64(scratch);
                                if (word == 0) continue;
                                if (api.WordContent(word, scratch) != 0) continue;
                                string wordText = Utf8(Marshal.ReadIntPtr(scratch));
                                if (String.IsNullOrEmpty(wordText)) continue;
                                words.Add(new OcrWord { Text = wordText, Box = ReadBox(word, false) });
                            }
                        }
                        if (words.Count == 0 && !String.IsNullOrEmpty(text)) words.Add(new OcrWord { Text = text, Box = lineBox });
                        if (String.IsNullOrEmpty(text)) {
                            var builder = new StringBuilder();
                            foreach (OcrWord word in words) builder.Append(word.Text);
                            text = builder.ToString();
                        }
                        if (String.IsNullOrEmpty(text)) continue;
                        var lineResult = new OcrLine { Box = lineBox, RawText = text };
                        lineResult.Words.AddRange(words);
                        document.Lines.Add(lineResult);
                    }
                    return document;
                } finally {
                    if (result != 0 && api != null && api.Release != null) api.Release(result);
                    handle.Free();
                }
            }
        }

        Rect ReadBox(long handle, bool line) {
            long code = line ? api.LineBox(handle, scratch) : api.WordBox(handle, scratch);
            if (code != 0) return Rect.Empty;
            IntPtr pointer = Marshal.ReadIntPtr(scratch);
            if (pointer == IntPtr.Zero) return Rect.Empty;
            var box = (OcrBox)Marshal.PtrToStructure(pointer, typeof(OcrBox));
            double left = Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4));
            double top = Math.Min(Math.Min(box.Y1, box.Y2), Math.Min(box.Y3, box.Y4));
            double right = Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4));
            double bottom = Math.Max(Math.Max(box.Y1, box.Y2), Math.Max(box.Y3, box.Y4));
            return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
        }

        static byte[] ToBgra(BitmapSource source, out int width, out int height) {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            width = converted.PixelWidth; height = converted.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);
            return pixels;
        }

        static string Utf8(IntPtr pointer) {
            if (pointer == IntPtr.Zero) return "";
            int length = 0;
            while (Marshal.ReadByte(pointer, length) != 0) length++;
            if (length == 0) return "";
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        static IntPtr Utf8(string text) {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.WriteByte(pointer, bytes.Length, 0);
            return pointer;
        }

        static void Check(long code, string message) {
            if (code != 0) throw new InvalidOperationException(message + "（错误码 " + code + "）");
        }

        T Bind<T>(string name) where T : class {
            IntPtr pointer = GetProcAddress(module, name);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("oneocr.dll 缺少函数：" + name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }

        public void Dispose() { Close(); }

        void Close() {
            if (api != null) {
                if (processOptions != 0) api.ReleaseProcess(processOptions);
                if (pipeline != 0) api.ReleasePipeline(pipeline);
                if (initOptions != 0) api.ReleaseInit(initOptions);
                processOptions = pipeline = initOptions = 0;
                api = null;
            }
            if (scratch != IntPtr.Zero) { Marshal.FreeHGlobal(scratch); scratch = IntPtr.Zero; }
        }

        static bool FindPackage(out string fullName, out string source) {
            fullName = null; source = null;
            try {
                uint count = 0, bufferLength = 0;
                int code = FindPackagesByPackageFamily(PackageFamily, 0x10, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero, IntPtr.Zero);
                if (count == 0) return false;
                var names = new IntPtr[count];
                var buffer = new char[bufferLength];
                GCHandle namesHandle = GCHandle.Alloc(names, GCHandleType.Pinned);
                GCHandle bufferHandle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try {
                    code = FindPackagesByPackageFamily(PackageFamily, 0x10, ref count, namesHandle.AddrOfPinnedObject(), ref bufferLength, bufferHandle.AddrOfPinnedObject(), IntPtr.Zero);
                    if (code != 0) return false;
                    int bestMajor = -1, bestMinor = -1, bestBuild = -1;
                    for (int i = 0; i < count; i++) {
                        string candidate = Marshal.PtrToStringUni(names[i]);
                        if (String.IsNullOrEmpty(candidate)) continue;
                        string path = PackagePath(candidate);
                        if (String.IsNullOrEmpty(path)) continue;
                        string directory = Path.Combine(path, "SnippingTool");
                        if (!HasFiles(directory)) continue;
                        int major, minor, build, revision;
                        Version(candidate, out major, out minor, out build, out revision);
                        if (major < bestMajor || (major == bestMajor && minor < bestMinor) || (major == bestMajor && minor == bestMinor && build < bestBuild)) continue;
                        bestMajor = major; bestMinor = minor; bestBuild = build;
                        fullName = candidate; source = directory;
                    }
                } finally {
                    bufferHandle.Free(); namesHandle.Free();
                }
            } catch { }
            if (source != null) return true;
            try {
                var info = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"(Get-AppxPackage -Name Microsoft.ScreenSketch).InstallLocation\"") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
                };
                using (var process = Process.Start(info)) {
                    string path = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);
                    if (!String.IsNullOrEmpty(path)) {
                        string directory = Path.Combine(path, "SnippingTool");
                        if (HasFiles(directory)) { fullName = Path.GetFileName(path.TrimEnd('\\')); source = directory; return true; }
                    }
                }
            } catch { }
            return false;
        }

        static string PackagePath(string fullName) {
            try {
                uint length = 0;
                GetPackagePathByFullName(fullName, ref length, null);
                if (length == 0) return null;
                var builder = new StringBuilder((int)length + 1);
                if (GetPackagePathByFullName(fullName, ref length, builder) != 0) return null;
                return builder.ToString();
            } catch { return null; }
        }

        static bool HasFiles(string directory) {
            return File.Exists(Path.Combine(directory, DllName)) && File.Exists(Path.Combine(directory, ModelName)) && File.Exists(Path.Combine(directory, RuntimeName));
        }

        static void Version(string fullName, out int major, out int minor, out int build, out int revision) {
            major = minor = build = revision = 0;
            string[] parts = fullName.Split('_');
            if (parts.Length < 2) return;
            string[] numbers = parts[1].Split('.');
            if (numbers.Length > 0) Int32.TryParse(numbers[0], out major);
            if (numbers.Length > 1) Int32.TryParse(numbers[1], out minor);
            if (numbers.Length > 2) Int32.TryParse(numbers[2], out build);
            if (numbers.Length > 3) Int32.TryParse(numbers[3], out revision);
        }

        static string PrepareFiles(string fullName, string source) {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShaiTu", "oneocr");
            string target = Path.Combine(root, fullName);
            if (HasFiles(target)) return target;
            Directory.CreateDirectory(root);
            string staging = target + ".tmp";
            try {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                foreach (string name in new[] { DllName, ModelName, RuntimeName })
                    File.Copy(Path.Combine(source, name), Path.Combine(staging, name), true);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(staging, target);
                return target;
            } catch (Exception ex) {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                throw new IOException("无法复制截图工具的 OCR 文件到缓存目录：" + ex.Message, ex);
            }
        }

        sealed class Api {
            public CreateOcrInitOptions CreateInit;
            public OcrInitOptionsSetUseModelDelayLoad SetDelayLoad;
            public CreateOcrPipeline CreatePipeline;
            public CreateOcrProcessOptions CreateProcessOptions;
            public OcrProcessOptionsSetMaxRecognitionLineCount SetMaxLines;
            public RunOcrPipeline Run;
            public GetOcrLineCount LineCount;
            public GetOcrLine Line;
            public GetOcrLineContent LineContent;
            public GetOcrLineBoundingBox LineBox;
            public GetOcrLineWordCount LineWordCount;
            public GetOcrWord Word;
            public GetOcrWordContent WordContent;
            public GetOcrWordBoundingBox WordBox;
            public Release Release, ReleaseInit, ReleasePipeline, ReleaseProcess;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct OcrImage { public int Type; public int Width; public int Height; public int Reserved; public long Step; public IntPtr Data; }

        [StructLayout(LayoutKind.Sequential)]
        struct OcrBox { public float X1, Y1, X2, Y2, X3, Y3, X4, Y4; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long CreateOcrInitOptions(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long OcrInitOptionsSetUseModelDelayLoad(long context, byte flag);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long CreateOcrPipeline(IntPtr modelPath, IntPtr key, long context, IntPtr pipeline);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long CreateOcrProcessOptions(IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long OcrProcessOptionsSetMaxRecognitionLineCount(long options, long count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long RunOcrPipeline(long pipeline, ref OcrImage image, long options, IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetOcrLineCount(long result, IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetOcrLine(long result, long index, IntPtr line);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetOcrLineContent(long line, IntPtr content);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetOcrLineBoundingBox(long line, IntPtr box);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetOcrLineWordCount(long line, IntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetOcrWord(long line, long index, IntPtr word);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetOcrWordContent(long word, IntPtr content);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long GetOcrWordBoundingBox(long word, IntPtr box);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Release(long handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectory(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int FindPackagesByPackageFamily(string family, uint filters, ref uint count, IntPtr names, ref uint bufferLength, IntPtr buffer, IntPtr properties);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackagePathByFullName(string fullName, ref uint pathLength, StringBuilder path);
    }
}
