using System;
using System.Windows;

namespace PathDock;

internal readonly record struct IslandFrame(Rect Bounds, double Radius, double SharpOpacity, double SoftOpacity, double IconOpacity);

internal static class IslandGeometry
{
    internal static double Smooth(double value) { var t = Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }
    internal static Rect Target(double width, double height, string side, double scale = 1)
    {
        var horizontal = side is "top" or "bottom";
        var w = Math.Round(width * scale); var h = Math.Round(height * scale);
        var ew = Math.Round((horizontal ? EdgeHandle.LongExtent : EdgeHandle.ShortExtent) * scale); var eh = Math.Round((horizontal ? EdgeHandle.ShortExtent : EdgeHandle.LongExtent) * scale);
        var x = horizontal ? Math.Round((w - ew) / 2) : side == "left" ? 0 : w - ew;
        var y = horizontal ? side == "top" ? 0 : h - eh : Math.Round((h - eh) / 2);
        return new(x / scale, y / scale, ew / scale, eh / scale);
    }
    internal static IslandFrame Sample(double width, double height, string side, double reveal, double scale = 1)
    {
        var t = Math.Clamp(reveal, 0, 1);
        // Suppress subpixel residue below .01 physical pixels near endpoints.
        if (t < .00001) t = 0;
        else if (t > .99999) t = 1;
        var target = Target(width, height, side, scale);
        var bounds = new Rect(target.X * (1 - t), target.Y * (1 - t),
            target.Width + (width - target.Width) * t, target.Height + (height - target.Height) * t);
        var sharp = Smooth((t - .24) / .48);
        var soft = (1 - sharp) * Smooth((t - .08) / .32);
        // The old radius grew with the shrinking card's short axis, then
        // contracted again: a visible bulge in the middle of a monotone motion.
        var radius = EdgeHandle.ShortExtent / 2 + (10 - EdgeHandle.ShortExtent / 2) * Smooth(t / .45);
        if (t > 0 && t < 1)
        {
            // Keep insignificant floating-point changes from choosing different
            // antialiasing buckets on fractional-DPI screens.
            double Stable(double value) => Math.Round(value * scale * 64) / (scale * 64);
            bounds = new Rect(Stable(bounds.X), Stable(bounds.Y), Stable(bounds.Width), Stable(bounds.Height));
            radius = Stable(radius);
        }
        return new(bounds, radius, sharp, soft, 1 - Smooth((t - .025) / .18));
    }
}
