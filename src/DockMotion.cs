using System;

namespace PathDock;

internal readonly record struct DockMotionFrame(double Position, double Velocity, bool Complete);

internal static class DockMotion
{
    // Finite, monotone easing. Reversals never continue towards the old target.
    internal static DockMotionFrame Sample(double start, double target, double velocity, double seconds)
    {
        var distance = target - start;
        if (Math.Abs(distance) < 1e-7) return new(target, 0, true);
        var duration = (target > .5 ? .32 : .35) * (.45 + .55 * Math.Sqrt(Math.Abs(distance)));
        var t = Math.Clamp(seconds / duration, 0, 1);
        if (t >= 1) return new(target, 0, true);
        // Clamp the forward tangent so the derivative remains nonnegative.
        // Opposing inertia is deliberately removed instead of creating recoil.
        var tangent = Math.Clamp(velocity * duration / distance, 0, 3);
        var remaining = 1 - t;
        var progress = t * t * (6 - 8 * t + 3 * t * t) + tangent * t * remaining * remaining * remaining;
        var slope = remaining * remaining * (12 * t + tangent * (1 - 4 * t));
        return new(start + distance * progress, distance * slope / duration, false);
    }
}
