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
internal sealed class CompletionModeController
{
    private readonly System.IServiceProvider _services;
    private readonly EnvDTE.DTE _dte;
    private readonly Microsoft.VisualStudio.Threading.JoinableTaskFactory _joinableTaskFactory;
    private readonly HashSet<string> _disabledByUs = new(StringComparer.OrdinalIgnoreCase);
    private readonly EnvDTE.WindowEvents _windowEvents; // must stay referenced or the COM event sink is collected

    public CompletionModeController(System.IServiceProvider services, EnvDTE.DTE dte, Microsoft.VisualStudio.Threading.JoinableTaskFactory joinableTaskFactory)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _services = services;
        _dte = dte;
        _joinableTaskFactory = joinableTaskFactory;
        _windowEvents = dte.Events.WindowEvents;
        _windowEvents.WindowActivated += OnWindowActivated;
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
        string? path;
        try
        {
            path = _dte.ActiveDocument?.FullName;
        }
        catch (ArgumentException)
        {
            path = null; // some tool windows report no document
        }

        if (path is null || !path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
        {
            return "No query window active; the mode will apply to each query window as you switch to it.";
        }

        var name = Path.GetFileName(path);
        var state = SsmsIntelliSense.IsEnabled(_services, _dte);
        string outcome;
        if (CompletionModeSettings.Mode == CompletionMode.Replace)
        {
            if (state == true && SsmsIntelliSense.Toggle(_dte))
            {
                _disabledByUs.Add(path);
                outcome = $"{name}: SSMS IntelliSense switched off.";
            }
            else
            {
                outcome = state == false ? $"{name}: SSMS IntelliSense already off." : $"{name}: SSMS IntelliSense state unknown; not changed.";
            }
        }
        else if (_disabledByUs.Contains(path))
        {
            if (state == false && SsmsIntelliSense.Toggle(_dte))
            {
                outcome = $"{name}: SSMS IntelliSense restored (we had switched it off).";
            }
            else
            {
                outcome = state == true ? $"{name}: SSMS IntelliSense already back on." : $"{name}: SSMS IntelliSense state unknown; restore it via Query > IntelliSense Enabled.";
            }

            _disabledByUs.Remove(path);
        }
        else
        {
            outcome = $"{name}: SSMS IntelliSense untouched ({(state is { } on ? (on ? "on" : "off") : "unknown")}).";
        }

        var after = SsmsIntelliSense.IsEnabled(_services, _dte);
        var line = $"Mode {CompletionModeSettings.Mode} applied on {reason}: {outcome} (now {(after is { } a ? (a ? "on" : "off") : "unknown")}).";
        SpikeLog.Info(line);
        return line;
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

        var relevant = path is not null && path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            && (CompletionModeSettings.Mode == CompletionMode.Replace || _disabledByUs.Contains(path));
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
