using System;

namespace PathDock;

internal static class DockMotionChecks
{
    internal static void Run(Action<string, Action> test, Action<bool> require)
    {
        test("Dock motion starts gently and settles without an aggressive initial jump", () => {
            foreach (var target in new[] { 0d, 1d })
            {
                var start = 1 - target;
                var first = DockMotion.Sample(start, target, 0, 0);
                require(first.Position == start && first.Velocity == 0 && !first.Complete);
                var previous = start;
                for (var i = 1; i <= 400; i++)
                {
                    var frame = DockMotion.Sample(start, target, 0, i / 1000d);
                    require(frame.Position >= 0 && frame.Position <= 1);
                    require(target == 1 ? frame.Position >= previous : frame.Position <= previous);
                    previous = frame.Position;
                }
                require(DockMotion.Sample(start, target, 0, .45).Complete);
                var earlyTravel = Math.Abs(DockMotion.Sample(start, target, 0, .05).Position - start);
                require(earlyTravel > (target == 0 ? .03 : .04) && earlyTravel < .1);
                require(!DockMotion.Sample(start, target, 0, .25).Complete);
            }
        });
        test("Closing keeps moving through the final quarter without changing opening", () => {
            var atThreeQuarters = DockMotion.Sample(1, 0, 0, .42 * .75);
            var nearEnd = DockMotion.Sample(1, 0, 0, .42 * .9);
            require(atThreeQuarters.Position > .15 && atThreeQuarters.Position < .17);
            require(nearEnd.Position > .02 && nearEnd.Position < .04);
            var opening = DockMotion.Sample(0, 1, 0, .4 * .5);
            require(Math.Abs(opening.Position - .6875) < 1e-10);
        });
        test("Reversals start from the displayed position without recoiling", () => {
            foreach (var target in new[] { 0d, 1d })
            foreach (var time in new[] { .005, .02, .06, .12, .20 })
            {
                var incoming = DockMotion.Sample(1 - target, target, 0, time);
                var reversed = DockMotion.Sample(incoming.Position, 1 - target, incoming.Velocity, 0);
                require(Math.Abs(reversed.Position - incoming.Position) < 1e-10);
                require(reversed.Velocity == 0);
                var previous = reversed.Position;
                for (var i = 1; i <= 400; i++)
                {
                    var after = DockMotion.Sample(incoming.Position, 1 - target, incoming.Velocity, i / 1000d);
                    require(target == 1 ? after.Position <= previous : after.Position >= previous);
                    previous = after.Position;
                }
                require(DockMotion.Sample(incoming.Position, 1 - target, incoming.Velocity, .5).Complete);
            }
        });
        test("Repeated reversals stay bounded and settle at different refresh rates", () => {
            foreach (var hz in new[] { 30, 60, 120, 144, 240 })
            {
                double position = 0, velocity = 0, target = 1;
                for (var i = 0; i < 100; i++)
                {
                    var frame = DockMotion.Sample(position, target, velocity, 1d / hz);
                    require(double.IsFinite(frame.Position) && double.IsFinite(frame.Velocity));
                    require(frame.Position >= 0 && frame.Position <= 1);
                    position = frame.Position; velocity = frame.Velocity; target = 1 - target;
                }
                var settled = DockMotion.Sample(position, target, velocity, .5);
                require(settled.Complete && settled.Position == target && settled.Velocity == 0);
            }
        });
        test("Near-endpoint reversals do not snap across a visible distance", () => {
            var frame = DockMotion.Sample(.03, 0, 0, 1d / 144);
            require(!frame.Complete && frame.Position > .02);
            require(DockMotion.Sample(.03, 0, 0, .35).Complete);
        });
    }
}
