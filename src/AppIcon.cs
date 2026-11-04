using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace PathDock;

internal static class AppIcon
{
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
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PathDock.Assets.PathDock.ico") ?? throw new FileNotFoundException("Application icon missing.");
        using var icon = new System.Drawing.Icon(stream, 16, 16);
        return (System.Drawing.Icon)icon.Clone();
    }
}
