using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PathDock;

internal sealed record ExplorerLocation(long Frame, long Browser, string BrowserClass, bool BrowserVisible, string Path, bool FileSystem, bool Foreground, int QueryResult, bool IsFolder = true);

internal static class DialogFollowRules
{
    internal static bool AddressLabelMatches(string name, string path) => name.TrimEnd('\\', '/')
        .EndsWith(path.Replace("&", "", StringComparison.Ordinal).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    internal static bool IsAddressEdit(string leafClass, bool visible, bool belongsToDialog, IEnumerable<string> ancestors)
        => visible && belongsToDialog && leafClass == "Edit"
            && ancestors.Any(c => c is "Address Band Root" or "Breadcrumb Parent" or "BreadcrumbParent");

    internal static (string Status, ExplorerLocation? Location) Select(long frame, IEnumerable<ExplorerLocation> source)
    {
        var candidates = source.Where(v => v.Frame == frame && v.QueryResult >= 0 && v.Browser > 0 && v.BrowserVisible
            && v.BrowserClass is "ShellTabWindowClass" or "CabinetWClass" or "ExploreWClass").ToArray();
        if (candidates.Length == 0) return ("NoActiveView", null);
        var unique = candidates.GroupBy(c => c.Browser).ToArray();
        if (unique.Length != 1 || unique[0].Select(v => v.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) return ("AmbiguousView", null);
        var selected = unique[0].First();
        if (!selected.FileSystem || !selected.IsFolder) return ("VirtualLocation", null);
        if (!ValidFileSystemPath(selected.Path)) return ("InvalidPath", null);
        return ("ResolvedCandidate", selected);
    }
    internal static bool ValidFileSystemPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) return false;
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase)) return false;
        try { return Path.IsPathFullyQualified(path); } catch (ArgumentException) { return false; }
    }
}

internal readonly record struct DialogIdentity(long Window, int Process, long Started);
internal sealed record NavigationIntent(DialogIdentity Target, long Visit, string Path, long ForegroundGeneration, long FilenameGeneration, long CreatedMs, long FolderGeneration = 0);
internal sealed record LiveState(DialogIdentity Target, bool Alive, bool Foreground, bool Supported, bool WritableIntegrity,
    bool DirectoryExists, bool IsDirectory, long ForegroundGeneration, long FilenameGeneration, bool ModifiersHeld, bool MouseHeld, int ReadinessWaitMs, long NowMs, long FolderGeneration = 0);

internal static class DialogCommitRules
{
    internal static string Check(NavigationIntent intent, LiveState live)
    {
        if (!live.Alive || live.Target != intent.Target) return "TargetChanged";
        if (!live.Foreground || live.ForegroundGeneration != intent.ForegroundGeneration) return "FocusChanged";
        if (!live.Supported) return "UnsupportedDialog";
        if (!live.WritableIntegrity) return "HigherIntegrity";
        if (live.NowMs < intent.CreatedMs || live.NowMs - intent.CreatedMs > 1000) return "Expired";
        if (live.FilenameGeneration != intent.FilenameGeneration) return "UserEdited";
        if (live.FolderGeneration != intent.FolderGeneration) return "UserNavigated";
        if (!live.DirectoryExists || !live.IsDirectory) return "DirectoryUnavailable";
        if (live.ModifiersHeld || live.MouseHeld) return live.ReadinessWaitMs < 300 ? "WaitInputRelease" : "InputBusy";
        return "Ready";
    }
}
