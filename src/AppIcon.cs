using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace PathDock;

internal static class AppIcon
{
    public static System.Windows.Media.DrawingImage Vector { get; } = CreateVector();
    static System.Windows.Media.DrawingImage CreateVector()
    {
        var group = new System.Windows.Media.DrawingGroup();
        using (var drawing = group.Open())
        {
            drawing.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new System.Windows.Rect(0, 0, 256, 256));
            drawing.DrawGeometry(Ui.Brush("#69B8F5"), null, System.Windows.Media.Geometry.Parse("M32,67 Q32,48 51,48 L100,48 Q108,48 114,55 L130,72 L205,72 Q224,72 224,91 L224,192 Q224,212 204,212 L52,212 Q32,212 32,192 Z"));
            drawing.DrawGeometry(new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(38, 141, 240), System.Windows.Media.Color.FromRgb(18, 97, 198), 90), null, System.Windows.Media.Geometry.Parse("M32,94 Q32,82 45,82 L211,82 Q224,82 224,95 L224,192 Q224,212 204,212 L52,212 Q32,212 32,192 Z"));
            var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 12) { StartLineCap = System.Windows.Media.PenLineCap.Round, EndLineCap = System.Windows.Media.PenLineCap.Round, LineJoin = System.Windows.Media.PenLineJoin.Round };
            drawing.DrawGeometry(null, pen, System.Windows.Media.Geometry.Parse("M78,165 L110,165 Q126,165 126,149 L126,139 Q126,123 142,123 L178,123"));
            drawing.DrawEllipse(System.Windows.Media.Brushes.White, null, new System.Windows.Point(78, 165), 11, 11);
            drawing.DrawEllipse(Ui.Brush("#77F1DB"), null, new System.Windows.Point(178, 123), 12, 12);
        }
        var image = new System.Windows.Media.DrawingImage(group); image.Freeze(); return image;
    }
    static BitmapImage? image;
    public static BitmapImage Image
    {
        get
        {
            if (image != null) return image;
            image = new BitmapImage(); image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Assets/PathDock.png");
            image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze(); return image;
        }
    }
    public static System.Drawing.Icon TrayIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PathDock.Assets.PathDock.Tray.ico") ?? throw new FileNotFoundException("Tray icon missing.");
        using var icon = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)icon.Clone();
    }
}
