using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace ShaiTu {
    internal static class AppIcons {
        const string ResourceName = "ShaiTu.AppIcon.ico";
        public static readonly BitmapFrame WindowIcon = LoadWindowIcon();
        static Stream Open() {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null) throw new InvalidOperationException("Application icon resource is missing.");
            return stream;
        }
        static BitmapFrame LoadWindowIcon() {
            using (var stream = Open()) {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame largest = decoder.Frames[0];
                foreach (var frame in decoder.Frames) if (frame.PixelWidth > largest.PixelWidth) largest = frame;
                largest.Freeze(); return largest;
            }
        }
        public static System.Drawing.Icon CreateTrayIcon() {
            using (var stream = Open())
            using (var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize))
                return (System.Drawing.Icon)icon.Clone();
        }
    }
}
