using System.Collections.Concurrent;
using System.Diagnostics;
using SqlCompanion.Server.Lsp;
using SqlCompanion.Spikes.S1.Engine;

namespace SqlCompanion.S1.EngineConnection.Tests;

/// <summary>Drives the SSMS adapter's engine client against the real <c>sqlwc-lsp</c> process.</summary>
public sealed class EngineConnectionTests : IDisposable
{
    private const string Uri = "file:///C:/Users/test/AppData/Local/Temp/SQLQuery1.sql";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly BlockingCollection<PublishedDiagnostics> _published = [];
    private readonly ConcurrentQueue<string> _log = new();
    private readonly SemaphoreSlim _ready = new(0);
    private string _text = string.Empty;
    private int _snapshot;

    private readonly Spikes.S1.Engine.EngineConnection _engine;

    public EngineConnectionTests()
    {
        _engine = Create(ServerStartInfo);
    }

    private static ProcessStartInfo ServerStartInfo()
    {
        // The engine assembly is copied next to the tests; run it with the shared runtime, as the VSIX's apphost does.
        var serverDll = typeof(LanguageServer).Assembly.Location;
        return new ProcessStartInfo("dotnet", $"\"{serverDll}\"");
    }

    private Spikes.S1.Engine.EngineConnection Create(Func<ProcessStartInfo> startInfo)
    {
        var engine = new Spikes.S1.Engine.EngineConnection(startInfo, _log.Enqueue, TimeSpan.FromMilliseconds(50));
        engine.DiagnosticsPublished += _published.Add;
        engine.Ready += _ => _ready.Release();
        return engine;
    }

    private DocumentText Capture() => new(_text, Interlocked.Increment(ref _snapshot));

    [Fact]
    public void Open_PublishesDiagnosticsWithTheSnapshotTagOfThatVersion()
    {
        _text = "SELECT 'oops";
        _engine.Open(Uri, Capture);

        var published = Next();
        Assert.Equal(Uri, published.Uri);
        Assert.Equal(1, published.Version);
        Assert.Equal(1, published.Tag);
        var diagnostic = Assert.Single(published.Diagnostics);
        Assert.Equal(("SQLC0001", 0, 7, 0, 12), (diagnostic.Code, diagnostic.StartLine, diagnostic.StartCharacter, diagnostic.EndLine, diagnostic.EndCharacter));
    }

    [Fact]
    public void RapidChanges_AreDebouncedIntoOneUpdate()
    {
        _text = "SELECT 1";
        _engine.Open(Uri, Capture);
        Next();

        foreach (var text in new[] { "SELECT '", "SELECT 'a", "SELECT 'ab", "SELECT 'abc'" })
        {
            _text = text;
            _engine.Change(Uri);
        }

        var published = Next();
        Assert.Equal(2, published.Version);
        Assert.Empty(published.Diagnostics);
        Assert.False(_published.TryTake(out _, 500, TestContext.Current.CancellationToken), "only one coalesced update expected");
        Assert.Contains("round trips n=2", _engine.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completion_SeesEditsMadeJustBeforeTheRequest()
    {
        _text = "DECLARE @Total INT;";
        _engine.Open(Uri, Capture);
        Next();

        // Typed within the debounce window: the request must flush it rather than complete against stale text.
        _text = "DECLARE @Total INT, @Count INT;\nSELECT ";
        _engine.Change(Uri);
        var items = await _engine.CompleteAsync(Uri, line: 1, character: 7, TestContext.Current.CancellationToken);

        Assert.Contains(items, i => i.Label == "@Count" && i.Kind == 6);
        Assert.Contains(items, i => i.Label == "SELECT" && i.Kind == 14);
    }

    [Fact]
    public async Task Completion_InsideAStringLiteral_IsEmpty()
    {
        _text = "SELECT 'abc";
        _engine.Open(Uri, Capture);
        Next();

        var items = await _engine.CompleteAsync(Uri, line: 0, character: 10, TestContext.Current.CancellationToken);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Completion_ForUnknownDocument_IsEmptyWithoutStartingTheEngine()
    {
        var items = await _engine.CompleteAsync("file:///never-opened.sql", 0, 0, TestContext.Current.CancellationToken);

        Assert.Empty(items);
        Assert.Null(_engine.ProcessId);
    }

    [Fact]
    public void KilledEngine_IsRestartedAndOpenDocumentsAreResynchronised()
    {
        _text = "SELECT [unterminated";
        _engine.Open(Uri, Capture);
        Next();
        Assert.True(_ready.Wait(Timeout, TestContext.Current.CancellationToken));
        var firstPid = _engine.ProcessId!.Value;

        Process.GetProcessById(firstPid).Kill();

        Assert.True(_ready.Wait(Timeout, TestContext.Current.CancellationToken), "engine did not become ready again: " + string.Join(" | ", _log));
        var republished = Next();
        Assert.Equal("SQLC0003", Assert.Single(republished.Diagnostics).Code);
        Assert.NotEqual(firstPid, _engine.ProcessId);
        Assert.Equal(1, _engine.RestartCount);
        Assert.Contains(_log, l => l.Contains("resynchronised 1 document", StringComparison.Ordinal));
    }

    [Fact]
    public void EngineThatCannotStart_GivesUpAfterRepeatedFailures()
    {
        using var broken = Create(() => new ProcessStartInfo(Path.Combine(Path.GetTempPath(), "does-not-exist", "sqlwc-lsp")));
        broken.EnsureStarted();

        var deadline = Stopwatch.StartNew();
        while (!broken.Status.Contains("FAILED", StringComparison.Ordinal) && deadline.Elapsed < Timeout)
        {
            TestContext.Current.CancellationToken.WaitHandle.WaitOne(100);
        }

        Assert.Contains("FAILED", broken.Status, StringComparison.Ordinal);
        Assert.Equal(Spikes.S1.Engine.EngineConnection.MaxRestartsPerMinute, broken.RestartCount);
    }

    [Fact]
    public void Dispose_StopsTheEngineProcess()
    {
        _text = "SELECT 1";
        _engine.Open(Uri, Capture);
        Next();
        var process = Process.GetProcessById(_engine.ProcessId!.Value);

        _engine.Dispose();

        Assert.True(process.WaitForExit(5000));
    }

    public void Dispose()
    {
        _engine.Dispose();
        _published.Dispose();
        _ready.Dispose();
    }

    private PublishedDiagnostics Next() =>
        _published.TryTake(out var published, (int)Timeout.TotalMilliseconds, TestContext.Current.CancellationToken)
            ? published
            : throw new TimeoutException("no diagnostics published. Log: " + string.Join(" | ", _log));
}
