using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Microsoft.VisualStudio.Shell.Interop;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// Evidence log for the spike: appends to %LOCALAPPDATA%\SqlCompanion\S1\s1-host.log and mirrors to the
/// "SQL Companion (S1)" Output pane once it exists. Thread-safe; never throws into the host.
/// </summary>
internal static class SpikeLog
{
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlCompanion", "S1", "s1-host.log");

    /// <summary>Distinct editor content types seen, with their base-type chain and how many views used them.</summary>
    public static ConcurrentDictionary<string, int> ContentTypes { get; } = new(StringComparer.Ordinal);

    public static IVsOutputWindowPane? Pane { get; set; }

    public static void Info(string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }

#pragma warning disable VSTHRD010 // OutputStringThreadSafe is documented as callable from any thread.
            Pane?.OutputStringThreadSafe(line + Environment.NewLine);
#pragma warning restore VSTHRD010
        }
        catch (Exception)
        {
            // Diagnostics must never destabilise SSMS.
        }
    }
}
