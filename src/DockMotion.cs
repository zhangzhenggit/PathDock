using System;

namespace PathDock;

internal readonly record struct DockMotionFrame(double Position, double Velocity, bool Complete);

internal static class DockMotion
{
    // Analytic critically damped motion: no frame-count dependency or spring
    // oscillation. A reversal retains the last displayed position and velocity.
    internal static DockMotionFrame Sample(double start, double target, double velocity, double seconds)
    {
        var time = Math.Max(0, seconds);
        // Full travel settles in roughly 220 ms opening / 240 ms closing.
        var rate = target > .5 ? 46d : 42d;
        var offset = start - target;
        var coefficient = velocity + rate * offset;
        var decay = Math.Exp(-rate * time);
        var position = target + (offset + coefficient * time) * decay;
        var speed = (velocity - rate * coefficient * time) * decay;
        // Inertia can reach the old endpoint during a rapid reversal. Never
        // expose pixels outside the dock's [edge, panel] range.
        if (position < 0 || position > 1) { position = Math.Clamp(position, 0, 1); speed = 0; }
        var complete = (Math.Abs(position - target) <= .0005 && Math.Abs(speed) <= .025) || time >= .5;
        return complete ? new(target, 0, true) : new(position, speed, false);
    }
}
