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
        engine.DiagnosticsPublished += published => DiagnosticsPublished?.Invoke(published);
        return engine;
    }
}
