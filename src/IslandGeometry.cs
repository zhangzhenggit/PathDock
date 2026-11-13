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
        var ew = Math.Round((horizontal ? 56 : 28) * scale); var eh = Math.Round((horizontal ? 28 : 56) * scale);
        var x = horizontal ? Math.Round((w - ew) / 2) : side == "left" ? 0 : w - ew;
        var y = horizontal ? side == "top" ? 0 : h - eh : Math.Round((h - eh) / 2);
        return new(x / scale, y / scale, ew / scale, eh / scale);
    }
    internal static IslandFrame Sample(double width, double height, string side, double reveal, double scale = 1)
    {
        var t = Math.Clamp(reveal, 0, 1);
        var target = Target(width, height, side, scale);
        var bounds = new Rect(target.X * (1 - t), target.Y * (1 - t),
            target.Width + (width - target.Width) * t, target.Height + (height - target.Height) * t);
        var sharp = Smooth((t - .24) / .48);
        var soft = (1 - sharp) * Smooth((t - .08) / .32);
        var capsuleRadius = Math.Min(bounds.Width, bounds.Height) / 2;
        var radius = capsuleRadius + (10 - capsuleRadius) * Smooth((t - .2) / .4);
        return new(bounds, radius, sharp, soft, 1 - Smooth((t - .025) / .18));
    }
}
