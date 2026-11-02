using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlCompanion.Spikes.S1;

/// <summary>How SQL Companion completion relates to SSMS IntelliSense (ADR-0006: Replace is the product direction).</summary>
internal enum CompletionMode
{
    /// <summary>SSMS IntelliSense only; SQL Companion completion inactive (safe default while ours is not schema-aware).</summary>
    Off,

    /// <summary>SQL Companion completion only; SSMS IntelliSense switched off via its own command, restored on leaving.</summary>
    Replace,

    /// <summary>S1 experiment: SSMS IntelliSense on, our items offered as an extra tab in its (legacy) completion list.</summary>
    MergeExperiment,
}

/// <summary>Persisted completion mode (per user). Which windows we switched off is per SSMS session, see <see cref="CompletionModeController"/>.</summary>
internal static class CompletionModeSettings
{
    private static readonly string FilePath = Path.Combine(Path.GetDirectoryName(SpikeLog.FilePath)!, "completion-mode.txt");

    static CompletionModeSettings()
    {
        try
        {
            var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : [];
            Mode = lines.Length > 0 && Enum.TryParse(lines[0], out CompletionMode mode) ? mode : CompletionMode.Off;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Mode = CompletionMode.Off;
        }
    }

    public static CompletionMode Mode { get; private set; }

    public static void Save(CompletionMode mode)
    {
        Mode = mode;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, [mode.ToString()]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SpikeLog.Info($"Could not persist completion mode: {exception.Message}");
        }
    }
}

/// <summary>
/// Applies the completion mode to SSMS's IntelliSense <b>per query window</b>: S1 showed the
/// <c>Query.IntelliSenseEnabled</c> toggle affects only the active window. Whenever a query window becomes active
/// (or the mode changes) the mode is applied to that window. Windows we switched off are remembered and restored
/// when next activated after leaving Replace — we never switch on a window we did not switch off.
/// </summary>
/// <remarks>
/// SSMS reports the toggle as unavailable while a query window is not ready (seen at start-up and before the
/// window is connected). Such a window is re-checked every second while it stays the active document, so it is
/// switched as soon as SSMS allows instead of waiting for the next activation. Closed windows are forgotten.
/// </remarks>
internal sealed class CompletionModeController
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    private readonly System.IServiceProvider _services;
    private readonly EnvDTE.DTE _dte;
    private readonly Microsoft.VisualStudio.Threading.JoinableTaskFactory _joinableTaskFactory;
    private readonly HashSet<string> _disabledByUs = new(StringComparer.OrdinalIgnoreCase);

    // Must stay referenced or the COM event sinks are collected.
    private readonly EnvDTE.WindowEvents _windowEvents;
    private readonly EnvDTE.DocumentEvents _documentEvents;

    // Incremented on every apply; a pending retry stops when it no longer matches.
    private int _generation;

    public CompletionModeController(System.IServiceProvider services, EnvDTE.DTE dte, Microsoft.VisualStudio.Threading.JoinableTaskFactory joinableTaskFactory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _services = services;
        _dte = dte;
        _joinableTaskFactory = joinableTaskFactory;
        _windowEvents = dte.Events.WindowEvents;
        _windowEvents.WindowActivated += OnWindowActivated;
        _documentEvents = dte.Events.DocumentEvents;
        _documentEvents.DocumentClosing += OnDocumentClosing;
    }

    public string Status
    {
        get
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var pending = CompletionModeSettings.Mode == CompletionMode.Replace ? "switched off by us" : "awaiting restore on next activation";
            return _disabledByUs.Count == 0
                ? "no query windows switched by us"
                : $"{_disabledByUs.Count} query window(s) {pending}: {string.Join(", ", _disabledByUs.Select(Path.GetFileName))}";
        }
    }

    /// <summary>Applies the current mode to the active query window, returning what was done (for the log/dialog).</summary>
    public string ApplyToActiveWindow(string reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var generation = ++_generation;
        var path = ActiveQueryPath();
        if (path is null)
        {
            return "No query window active; the mode will apply to each query window as you switch to it.";
        }

        var (line, unavailable) = Apply(path, reason);
        SpikeLog.Info(line);
        if (unavailable)
        {
            SpikeLog.Info($"{Path.GetFileName(path)}: SSMS IntelliSense toggle not available yet; re-checking every {RetryInterval.TotalSeconds:0} s while this window stays active.");
            _ = _joinableTaskFactory.RunAsync(() => RetryWhileUnavailableAsync(path, generation));
        }

        return line;
    }

    /// <summary>Applies the mode to <paramref name="path"/> (the active query window); <c>unavailable</c> when SSMS did not report the toggle state and an action was due.</summary>
    private (string Line, bool Unavailable) Apply(string path, string reason)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var name = Path.GetFileName(path);
        var state = SsmsIntelliSense.IsEnabled(_services, _dte);
        var unavailable = false;
        string outcome;
        if (CompletionModeSettings.Mode == CompletionMode.Replace)
        {
            if (state == true && SsmsIntelliSense.Toggle(_dte))
            {
                _disabledByUs.Add(path);
                outcome = $"{name}: SSMS IntelliSense switched off.";
            }
            else if (state == false)
            {
                outcome = $"{name}: SSMS IntelliSense already off.";
            }
            else
            {
                unavailable = true;
                outcome = $"{name}: SSMS IntelliSense state unknown; not changed yet.";
            }
        }
        else if (_disabledByUs.Contains(path))
        {
            if (state == false && SsmsIntelliSense.Toggle(_dte))
            {
                _disabledByUs.Remove(path);
                outcome = $"{name}: SSMS IntelliSense restored (we had switched it off).";
            }
            else if (state == true)
            {
                _disabledByUs.Remove(path);
                outcome = $"{name}: SSMS IntelliSense already back on.";
            }
            else
            {
                unavailable = true; // keep the record: the restore is still owed
                outcome = $"{name}: SSMS IntelliSense state unknown; restore pending.";
            }
        }
        else
        {
            outcome = $"{name}: SSMS IntelliSense untouched ({Describe(state)}).";
        }

        var after = SsmsIntelliSense.IsEnabled(_services, _dte);
        return ($"Mode {CompletionModeSettings.Mode} applied on {reason}: {outcome} (now {Describe(after)}).", unavailable);
    }

    private async System.Threading.Tasks.Task RetryWhileUnavailableAsync(string path, int generation)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            await System.Threading.Tasks.Task.Delay(RetryInterval).ConfigureAwait(false);
            waited += RetryInterval;
            await _joinableTaskFactory.SwitchToMainThreadAsync();
            if (generation != _generation || !string.Equals(ActiveQueryPath(), path, StringComparison.OrdinalIgnoreCase))
            {
                return; // superseded by a newer apply, or the user moved to another document
            }

            var (line, unavailable) = Apply(path, $"retry after {waited.TotalSeconds:0} s");
            if (!unavailable)
            {
                SpikeLog.Info(line);
                return;
            }
        }
    }

    private void OnWindowActivated(EnvDTE.Window gotFocus, EnvDTE.Window lostFocus)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? path;
        try
        {
            path = gotFocus?.Document?.FullName;
        }
        catch (ArgumentException)
        {
            return;
        }

        var relevant = IsQueryPath(path)
            && (CompletionModeSettings.Mode == CompletionMode.Replace || _disabledByUs.Contains(path!));
        if (!relevant)
        {
            return;
        }

        // Let activation finish before executing the toggle command against the newly active window.
        _ = _joinableTaskFactory.RunAsync(async () =>
        {
            await _joinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
            ApplyToActiveWindow("window activation");
        });
    }

    private void OnDocumentClosing(EnvDTE.Document document)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string? path;
        try
        {
            path = document?.FullName;
        }
        catch (ArgumentException)
        {
            return;
        }

        // The window's IntelliSense state ends with it; keeping the path would let a later window with the same
        // path be "restored" against the user's choice.
        if (path is not null && _disabledByUs.Remove(path))
        {
            SpikeLog.Info($"{Path.GetFileName(path)}: closed; forgotten by the completion mode controller.");
        }
    }

    private string? ActiveQueryPath()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var path = _dte.ActiveDocument?.FullName;
            return IsQueryPath(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null; // some tool windows report no document
        }
    }

    private static bool IsQueryPath(string? path) => path is not null && path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    private static string Describe(bool? state) => state is { } on ? (on ? "on" : "off") : "unknown";
}

/// <summary>
/// Reads and flips SSMS IntelliSense through SSMS's own command (Query → IntelliSense Enabled), never through
/// private settings. The command's latched state is the source of truth; it is only meaningful while a query
/// window is active.
/// </summary>
internal static class SsmsIntelliSense
{
    private const string ToggleCommandName = "Query.IntelliSenseEnabled";

    /// <summary>Every shell command whose name mentions IntelliSense, with availability — S1 evidence.</summary>
    public static string DescribeCommands(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var found = new List<string>();
        foreach (EnvDTE.Command command in dte.Commands)
        {
            var name = command.Name;
            if (!string.IsNullOrEmpty(name) && name.IndexOf("intellisense", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found.Add($"  {name}  guid={command.Guid} id={command.ID} available={command.IsAvailable}");
            }
        }

        return found.Count == 0 ? "  (no IntelliSense commands found)" : string.Join(Environment.NewLine, found.OrderBy(f => f, StringComparer.Ordinal));
    }

    /// <summary>True/false when SSMS reports the toggle's state; null if the command is missing or not applicable here.</summary>
    public static bool? IsEnabled(System.IServiceProvider services, EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var command = Find(dte);
        if (command is null || services.GetService(typeof(SUIHostCommandDispatcher)) is not IOleCommandTarget dispatcher)
        {
            return null;
        }

        var group = new Guid(command.Guid);
        var commands = new[] { new OLECMD { cmdID = (uint)command.ID } };
        if (dispatcher.QueryStatus(ref group, 1, commands, IntPtr.Zero) != 0)
        {
            return null;
        }

        var flags = (OLECMDF)commands[0].cmdf;
        if ((flags & OLECMDF.OLECMDF_ENABLED) == 0)
        {
            return null; // not applicable (e.g. no query window active)
        }

        return (flags & OLECMDF.OLECMDF_LATCHED) != 0;
    }

    public static bool Toggle(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Find(dte) is null)
        {
            return false;
        }

        dte.ExecuteCommand(ToggleCommandName);
        return true;
    }

    private static EnvDTE.Command? Find(EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            return dte.Commands.Item(ToggleCommandName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
