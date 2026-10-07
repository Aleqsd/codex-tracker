using System;
using System.IO;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using DrawingFont = System.Drawing.Font;

namespace CodexTracker.App;

internal static class AppTypography
{
    private static readonly string ResourceRoot = $"pack://application:,,,/{typeof(AppTypography).Assembly.GetName().Name};component/Assets/Fonts/";
    internal static readonly FontFamily Family = new(new Uri("pack://application:,,,/"), $"/{typeof(AppTypography).Assembly.GetName().Name};component/Assets/Fonts/#Roboto");
    internal static readonly Typeface TrayTypeface = new(Family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    // ToolStrip uses GDI, whereas WPF resolves the embedded font directly.
    // This registration is private to this process and lasts only as long as the tray.
    internal sealed class MenuFont : IDisposable
    {
        private readonly PrivateFontCollection _fonts = new();
        private IntPtr _data, _registration;
        internal DrawingFont Font { get; private set; } = null!;

        internal MenuFont()
        {
            try
            {
                using var source = Application.GetResourceStream(new Uri(ResourceRoot + "Roboto-Regular.ttf"))!.Stream;
                using var buffer = new MemoryStream(); source.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();
                _data = Marshal.AllocCoTaskMem(bytes.Length); Marshal.Copy(bytes, 0, _data, bytes.Length);
                _fonts.AddMemoryFont(_data, bytes.Length);
                _registration = AddFontMemResourceEx(_data, (uint)bytes.Length, IntPtr.Zero, out uint count);
                if (_registration == IntPtr.Zero || count == 0) throw new InvalidOperationException("La police du menu n’a pas pu être chargée.");
                using var family = _fonts.Families[0];
                Font = new DrawingFont(family, 9f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            Font?.Dispose(); _fonts.Dispose();
            if (_registration != IntPtr.Zero) { RemoveFontMemResourceEx(_registration); _registration = IntPtr.Zero; }
            if (_data != IntPtr.Zero) { Marshal.FreeCoTaskMem(_data); _data = IntPtr.Zero; }
        }

        [DllImport("gdi32.dll")] private static extern IntPtr AddFontMemResourceEx(IntPtr data, uint length, IntPtr reserved, out uint count);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveFontMemResourceEx(IntPtr handle);
    }
}
