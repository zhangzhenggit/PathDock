using System;
using System.Windows;

namespace PathDock;

internal static class IslandGeometryChecks
{
    internal static void Run(Action<string, Action> test, Action<bool> require)
    {
        test("Island landing aligns to physical pixels at mixed monitor scales", () => {
            foreach (var side in new[] { "left", "right", "top", "bottom" })
            foreach (var scale in new[] { 1d, 1.25, 1.3, 1.5, 1.75, 2 })
            {
                var target = IslandGeometry.Target(300, 598.5, side, scale);
                foreach (var value in new[] { target.X, target.Y, target.Width, target.Height })
                    require(Math.Abs(value * scale - Math.Round(value * scale)) < 1e-8);
                require(IslandGeometry.Sample(300, 598.5, side, 0, scale).Bounds == target);
            }
        });
        test("Island card endpoints match panel and edge capsule on all sides", () => {
            foreach (var side in new[] { "left", "right", "top", "bottom" })
            foreach (var height in new[] { 300d, 582, 950 })
            {
                var open = IslandGeometry.Sample(300, height, side, 1);
                var shut = IslandGeometry.Sample(300, height, side, 0);
                require(open.Bounds == new Rect(0, 0, 300, height) && open.SharpOpacity == 1 && open.IconOpacity == 0);
                require(shut.Bounds == IslandGeometry.Target(300, height, side) && shut.IconOpacity == 1);
                require(shut.SharpOpacity == 0 && shut.SoftOpacity == 0 && shut.Radius == EdgeHandle.ShortExtent / 2);
            }
        });
        test("Island contraction converges monotonically without a separate corner squeeze", () => {
            foreach (var side in new[] { "left", "right", "top", "bottom" })
            {
                var previous = IslandGeometry.Sample(300, 600, side, 1);
                for (var i = 99; i >= 0; i--)
                {
                    var current = IslandGeometry.Sample(300, 600, side, i / 100d);
                    require(current.Bounds.Width <= previous.Bounds.Width && current.Bounds.Height <= previous.Bounds.Height);
                    require(current.Radius >= 10 && current.Radius <= Math.Min(current.Bounds.Width, current.Bounds.Height) / 2);
                    require(current.Bounds.Left >= previous.Bounds.Left && current.Bounds.Top >= previous.Bounds.Top);
                    previous = current;
                }
            }
        });
        test("Corner radius changes monotonically without a mid-transition bulge", () => {
            foreach (var side in new[] { "left", "right", "top", "bottom" })
            {
                var previous = EdgeHandle.ShortExtent / 2;
                for (var i = 0; i <= 1000; i++)
                {
                    var frame = IslandGeometry.Sample(300, 600, side, i / 1000d);
                    require(frame.Radius <= previous && frame.Radius >= 10);
                    previous = frame.Radius;
                }
            }
        });
        test("Island content dissolves before the destination icon becomes dominant", () => {
            require(IslandGeometry.Sample(300, 600, "right", .1).SharpOpacity == 0);
            for (var i = 0; i <= 100; i++)
            {
                var frame = IslandGeometry.Sample(300, 600, "right", i / 100d);
                require(frame.SharpOpacity is >= 0 and <= 1 && frame.SoftOpacity is >= 0 and <= 1 && frame.IconOpacity is >= 0 and <= 1);
                if (frame.IconOpacity > .5) require(frame.SharpOpacity == 0);
            }
        });
    }
}
