using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace PathDock;

internal enum InputKind { Unknown, TextBox, Codex }
internal sealed record InputObservation(InputKind Kind, string FocusId, IntPtr Window);

internal static class AutoTarget
{
    static Task<InputObservation>? pending;
    static readonly object gate = new();

    public static async Task<InputObservation> Observe(TargetWindow target)
    {
        Task<InputObservation> task;
        lock (gate)
        {
            // A hung accessibility provider must not spawn a new worker on every click.
            if (pending is { IsCompleted: false }) return new(InputKind.Unknown, "", target.Handle);
            task = pending = Task.Run(() => Read(target));
        }
        try { return await task.WaitAsync(TimeSpan.FromMilliseconds(900)); }
        catch { return new(InputKind.Unknown, "", target.Handle); }
    }

    static InputObservation Read(TargetWindow target)
    {
        try
        {
            if (Native.GetForegroundWindow() != target.Handle) return new(InputKind.Unknown, "", target.Handle);
            var focused = AutomationElement.FocusedElement;
            if (focused == null || focused.Current.ProcessId != target.Pid || !focused.Current.HasKeyboardFocus || focused.Current.IsPassword || !focused.Current.IsEnabled) return new(InputKind.Unknown, "", target.Handle);
            var identity = string.Join(".", focused.GetRuntimeId());
            if (!target.IsTerminal)
            {
                if (focused.Current.ControlType == ControlType.Edit && focused.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && !((ValuePattern)value).Current.IsReadOnly)
                    return new(InputKind.TextBox, identity, target.Handle);
                return new(InputKind.Unknown, identity, target.Handle);
            }
            if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
            {
                var range = ((TextPattern)pattern).DocumentRange.Clone();
                range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -12000);
                var all = range.GetText(12000);
                // Some terminal providers ignore endpoint moves. Bound the text used
                // for classification and never persist terminal content.
                var tail = all.Length > 12000 ? all[^12000..] : all;
                if (LooksLikeCodexInput(tail) && Native.HasDescendant(target.Pid, "codex.exe")) return new(InputKind.Codex, identity, target.Handle);
            }
            return new(InputKind.Unknown, identity, target.Handle);
        }
        catch { return new(InputKind.Unknown, "", target.Handle); }
    }

    internal static bool LooksLikeCodexInput(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).TakeLast(160).ToArray();
        if (lines.Length == 0) return false;
        var last = string.Join("\n", lines.TakeLast(4));
        if (Regex.IsMatch(last, @"(?im)^\s*(?:PS\s+)?[A-Z]:\\[^\n]*>\s*.*$") || Regex.IsMatch(last, @"(?im)^\s*(?:>>>|\.\.\.|[\w.-]+@[^\n]+[$#])\s*")) return false;
        var footer = lines.TakeLast(4).Any(l => l.Contains("? for shortcuts", StringComparison.OrdinalIgnoreCase));
        var promptIndex = Array.FindLastIndex(lines, l => Regex.IsMatch(l, @"^\s*[›❯]\s*"));
        if (promptIndex < 0) return false;
        var contextIndex = Array.FindLastIndex(lines, l => l.Contains("context", StringComparison.OrdinalIgnoreCase) && (l.Contains("left", StringComparison.OrdinalIgnoreCase) || l.Contains("%")));
        // Current composer status must follow the current prompt. A nonempty
        // draft changes Codex's footer hints and must not require the idle hint.
        if (contextIndex <= promptIndex || contextIndex < lines.Length - 8) return false;
        var firstLine = Regex.Replace(lines[promptIndex], @"^\s*[›❯]\s*", "");
        var hasDraft = !string.IsNullOrWhiteSpace(firstLine) && !firstLine.StartsWith("Ask Codex", StringComparison.OrdinalIgnoreCase);
        hasDraft |= lines.Skip(promptIndex + 1).Take(contextIndex - promptIndex - 1).Any(l => !l.Contains("for shortcuts", StringComparison.OrdinalIgnoreCase));
        return footer || hasDraft;
    }
}
