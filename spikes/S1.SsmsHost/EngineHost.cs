using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.Text;
using SqlCompanion.Spikes.S1.Engine;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// One engine process per SSMS instance (S1 step 2). The engine is the .NET 10 <c>sqlwc-lsp</c> shipped in the
/// VSIX under <c>server\</c>; SSMS runs .NET Framework 4.8, so it must be out of process.
/// </summary>
internal static class EngineHost
{
    /// <summary>Buffer property key holding the document URI registered with the engine.</summary>
    public static readonly object DocumentUriKey = new();

    private static readonly Lazy<EngineConnection> Instance = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Latest diagnostics per document URI; taggers listen for changes.</summary>
    public static event Action<PublishedDiagnostics>? DiagnosticsPublished;

    public static EngineConnection Engine => Instance.Value;

    public static bool IsCreated => Instance.IsValueCreated;

    public static string ServerPath { get; } =
        Path.Combine(Path.GetDirectoryName(typeof(EngineHost).Assembly.Location)!, "server", "sqlwc-lsp.exe");

    public static string? UriOf(ITextBuffer buffer) =>
        buffer.Properties.TryGetProperty(DocumentUriKey, out string uri) ? uri : null;

    private static EngineConnection Create()
    {
        if (!File.Exists(ServerPath))
        {
            SpikeLog.Info($"Engine executable missing: {ServerPath}");
        }

        var engine = new EngineConnection(() => new ProcessStartInfo(ServerPath), SpikeLog.Info, TimeSpan.FromMilliseconds(150));
        engine.DiagnosticsPublished += published =>
        {
            LogDiagnosticsChange(published, engine);
            DiagnosticsPublished?.Invoke(published);
        };
        return engine;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> LastDiagnosticCount = new(StringComparer.Ordinal);
    private static int _published;

    /// <summary>
    /// Evidence for step 2: logs the first result per document and every change in its diagnostic count
    /// (a squiggle appearing or disappearing), plus a latency summary every 25 results. No query text is logged.
    /// </summary>
    private static void LogDiagnosticsChange(PublishedDiagnostics published, EngineConnection engine)
    {
        var count = published.Diagnostics.Count;
        var previous = LastDiagnosticCount.TryGetValue(published.Uri, out var last) ? last : -1;
        LastDiagnosticCount[published.Uri] = count;
        if (count != previous)
        {
            var codes = string.Join(",", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(published.Diagnostics, d => d.Code)));
            SpikeLog.Info($"Diagnostics for {published.Uri} v{published.Version}: {count} ({(count == 0 ? "clear" : codes)}).");
        }

        if (Interlocked.Increment(ref _published) % 25 == 0)
        {
            SpikeLog.Info($"Engine status: {engine.Status}");
        }
    }
}
