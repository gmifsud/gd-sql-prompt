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

/// <summary>Persisted completion mode, plus whether SQL Companion is the one that switched SSMS IntelliSense off.</summary>
internal static class CompletionModeSettings
{
    private static readonly string FilePath = Path.Combine(Path.GetDirectoryName(SpikeLog.FilePath)!, "completion-mode.txt");

    static CompletionModeSettings()
    {
        try
        {
            var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : [];
            Mode = lines.Length > 0 && Enum.TryParse(lines[0], out CompletionMode mode) ? mode : CompletionMode.Off;
            WeDisabledSsmsIntelliSense = lines.Length > 1 && lines[1] == "we-disabled-ssms-intellisense";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Mode = CompletionMode.Off;
        }
    }

    public static CompletionMode Mode { get; private set; }

    public static bool WeDisabledSsmsIntelliSense { get; private set; }

    public static void Save(CompletionMode mode, bool weDisabledSsmsIntelliSense)
    {
        Mode = mode;
        WeDisabledSsmsIntelliSense = weDisabledSsmsIntelliSense;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, [mode.ToString(), weDisabledSsmsIntelliSense ? "we-disabled-ssms-intellisense" : "ssms-intellisense-untouched"]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SpikeLog.Info($"Could not persist completion mode: {exception.Message}");
        }
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
