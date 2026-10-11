using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace ShaiTu {
    // A request-scoped receiver, independent of the persistent clipboard reception setting.
    public sealed class WindowsCaptureSession {
        public bool Active { get; private set; }
        public DateTime Deadline { get; private set; }
        uint initialSequence;
        public void Start(DateTime now, uint sequence, Action launch) {
            initialSequence = sequence; Deadline = now.AddMinutes(2); Active = true;
            try { launch(); } catch { Cancel(); throw; }
        }
        public void Cancel() { Active = false; }
        public bool Expire(DateTime now) {
            if (!Active || now < Deadline) return false;
            Cancel(); return true;
        }
        public static bool IsWindowsOwner(string name) {
            return String.Equals(name, "SnippingTool", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "ScreenClippingHost", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "ScreenSketch", StringComparison.OrdinalIgnoreCase)
                || String.Equals(name, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase);
        }
        public bool TryReceive(DateTime now, uint sequence, uint ownSequence, string owner,
            Func<BitmapSource> read, Action<BitmapSource> save) {
            if (!Active || now >= Deadline || sequence == initialSequence || sequence == ownSequence || !IsWindowsOwner(owner)) return false;
            BitmapSource image = read(); if (image == null) return false;
            save(image); Cancel(); return true;
        }
    }
    internal static class WindowsSnipCommands {
        [StructLayout(LayoutKind.Sequential)] struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] struct InputData {
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public InputData Data; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        public static bool EscapeDown { get { return (GetAsyncKeyState(27) & 0x8000) != 0; } }
        internal static int NativeInputSize { get { return Marshal.SizeOf(typeof(Input)); } }
        public static void Execute() {
            // Win+Shift+S is the Windows 10/11 shell entry point; no MSIX identity or callback protocol is needed.
            foreach (int key in new[] { 16, 17, 18, 91, 92 }) if ((GetAsyncKeyState(key) & 0x8000) != 0)
                throw new InvalidOperationException("请松开 Ctrl、Alt、Shift、Win，再点击 Windows 截图。");
            ushort[] keys = { 91, 16, 83, 83, 16, 91 };
            var inputs = new Input[keys.Length];
            for (int i = 0; i < keys.Length; i++) inputs[i] = new Input {
                Type = 1, Data = new InputData { Keyboard = new KeyboardInput { Key = keys[i], Flags = i >= 3 ? 2u : 0u } }
            };
            if (SendInput((uint)inputs.Length, inputs, NativeInputSize) != inputs.Length) {
                // Release synthetic keys even if Windows accepted only part of the batch.
                SendInput(3, new[] { inputs[3], inputs[4], inputs[5] }, NativeInputSize);
                throw new InvalidOperationException("无法打开 Windows 截图，请使用 Win + Shift + S 或托盘添加图片。");
            }
        }
    }
}
