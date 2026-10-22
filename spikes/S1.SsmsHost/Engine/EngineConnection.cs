using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StreamJsonRpc;

namespace SqlCompanion.Spikes.S1.Engine;

/// <summary>A diagnostic reported by the engine, in LSP coordinates (zero-based line, UTF-16 character).</summary>
public sealed class EngineDiagnostic
{
    public EngineDiagnostic(int startLine, int startCharacter, int endLine, int endCharacter, string code, string message, int severity)
    {
        StartLine = startLine;
        StartCharacter = startCharacter;
        EndLine = endLine;
        EndCharacter = endCharacter;
        Code = code;
        Message = message;
        Severity = severity;
    }

    public int StartLine { get; }

    public int StartCharacter { get; }

    public int EndLine { get; }

    public int EndCharacter { get; }

    public string Code { get; }

    public string Message { get; }

    /// <summary>LSP severity: 1 error, 2 warning, 3 information, 4 hint.</summary>
    public int Severity { get; }
}

/// <summary>Text of a document at one moment, plus an opaque host object (the editor snapshot) to map results back.</summary>
public sealed class DocumentText
{
    public DocumentText(string text, object? tag)
    {
        Text = text;
        Tag = tag;
    }

    public string Text { get; }

    public object? Tag { get; }
}

/// <summary>Diagnostics for one document version, with the host tag captured when that version was sent.</summary>
public sealed class PublishedDiagnostics
{
    public PublishedDiagnostics(string uri, int? version, object? tag, IReadOnlyList<EngineDiagnostic> diagnostics)
    {
        Uri = uri;
        Version = version;
        Tag = tag;
        Diagnostics = diagnostics;
    }

    public string Uri { get; }

    public int? Version { get; }

    public object? Tag { get; }

    public IReadOnlyList<EngineDiagnostic> Diagnostics { get; }
}

/// <summary>
/// Host-independent client for the out-of-process engine (<c>sqlwc-lsp</c>) over LSP/JSON-RPC on stdio.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Never blocks the caller: every operation is queued to the thread pool.</item>
/// <item>Edits are debounced per document and sent as full-text <c>didChange</c> with increasing versions.</item>
/// <item>If the engine exits or the connection drops, it is restarted with back-off and every open document is
/// re-sent (<c>didOpen</c> with its current text). More than <see cref="MaxRestartsPerMinute"/> failures stop it.</item>
/// <item>On Windows the engine runs in a kill-on-close job object, so it cannot outlive the host process.</item>
/// <item>Results for superseded versions are dropped; round-trip latency (send → diagnostics) is recorded.</item>
/// </list>
/// Compiles for .NET Framework 4.8 (inside SSMS) and modern .NET (tests).
/// </remarks>
public sealed class EngineConnection : IDisposable
{
    public const int MaxRestartsPerMinute = 5;

    private readonly Func<ProcessStartInfo> _startInfo;
    private readonly Action<string> _log;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private readonly List<DateTime> _restarts = [];
    private readonly List<double> _roundTripsMs = [];

    private Process? _process;
    private JsonRpc? _rpc;
    private JobObject? _job;
    private Task _ready = Task.CompletedTask;
    private int _generation;
    private bool _disposed;
    private bool _failed;
    private Stopwatch? _downtime;
    private double? _lastRestartMs;

    public EngineConnection(Func<ProcessStartInfo> startInfo, Action<string> log, TimeSpan debounce)
    {
        _startInfo = startInfo ?? throw new ArgumentNullException(nameof(startInfo));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _debounce = debounce;
    }

    /// <summary>Raised on a thread-pool thread whenever the engine publishes diagnostics for an open document.</summary>
    public event Action<PublishedDiagnostics>? DiagnosticsPublished;

    /// <summary>Raised on a thread-pool thread after the engine (re)starts and documents are resynchronised.</summary>
    public event Action<int>? Ready;

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                try
                {
                    return _process is { HasExited: false } ? _process.Id : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }
    }

    public int RestartCount
    {
        get
        {
            lock (_gate)
            {
                return _restarts.Count;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                var state = _failed ? "FAILED (gave up after repeated crashes)" : _process is null ? "not started" : "running";
                var latency = _roundTripsMs.Count == 0
                    ? "no round trips yet"
                    : $"round trips n={_roundTripsMs.Count} p50={Percentile(0.50):0} ms p95={Percentile(0.95):0} ms max={_roundTripsMs.Max():0} ms";
                var restart = _lastRestartMs is { } ms ? $" last restart {ms:0} ms;" : string.Empty;
                return $"engine {state}, pid {ProcessId?.ToString() ?? "-"}, restarts {_restarts.Count};{restart} documents {_documents.Count}; {latency}";
            }
        }
    }

    /// <summary>Starts the engine if it is not running. Safe to call repeatedly.</summary>
    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed || _failed || _process is not null)
            {
                return;
            }

            StartLocked();
        }
    }

    public void Open(string uri, Func<DocumentText> currentText)
    {
        OpenDocument document;
        lock (_gate)
        {
            if (_disposed || _documents.ContainsKey(uri))
            {
                return;
            }

            document = new OpenDocument(uri, currentText);
            _documents[uri] = document;
        }

        EnsureStarted();
        _ = Task.Run(() => SendOpenAsync(document));
    }

    /// <summary>Schedules a debounced full-text update; rapid edits coalesce into one message.</summary>
    public void Change(string uri)
    {
        lock (_gate)
        {
            if (_disposed || !_documents.TryGetValue(uri, out var document))
            {
                return;
            }

            document.Debounce ??= new Timer(_ => _ = Task.Run(() => SendChangeAsync(document)), null, Timeout.Infinite, Timeout.Infinite);
            document.Debounce.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Close(string uri)
    {
        OpenDocument? document;
        lock (_gate)
        {
            if (!_documents.TryGetValue(uri, out document))
            {
                return;
            }

            _documents.Remove(uri);
            document.Debounce?.Dispose();
        }

        _ = Task.Run(() => SendAsync(rpc => rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri } })));
    }

    public void Dispose()
    {
        Process? process;
        JsonRpc? rpc;
        JobObject? job;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var document in _documents.Values)
            {
                document.Debounce?.Dispose();
            }

            _documents.Clear();
            (process, rpc, job) = (_process, _rpc, _job);
            (_process, _rpc, _job) = (null, null, null);
        }

        try
        {
            if (rpc is not null)
            {
                // Polite LSP shutdown, bounded so disposal never hangs the host.
                var shutdown = rpc.InvokeAsync<object?>("shutdown");
                if (shutdown.Wait(TimeSpan.FromSeconds(1)))
                {
                    rpc.NotifyAsync("exit").Wait(TimeSpan.FromMilliseconds(500));
                }
            }
        }
        catch (Exception exception) when (exception is AggregateException or ConnectionLostException or ObjectDisposedException)
        {
        }

        try
        {
            if (process is { HasExited: false } && !process.WaitForExit(1000))
            {
                process.Kill();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        rpc?.Dispose();
        process?.Dispose();
        job?.Dispose();
        _log("Engine stopped.");
    }

    // ───────────────────────────── lifecycle ─────────────────────────────

    private void StartLocked()
    {
        var generation = ++_generation;
        Process process;
        try
        {
            var info = _startInfo();
            info.UseShellExecute = false;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.CreateNoWindow = true;
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            _log($"Engine failed to start: {exception.GetType().Name}: {exception.Message}");
            ScheduleRestartLocked(generation);
            return;
        }

        if (JobObject.IsSupported)
        {
            _job ??= new JobObject();
            _job.Assign(process);
        }

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _log($"engine stderr: {e.Data}");
            }
        };
        process.BeginErrorReadLine();
        process.Exited += (_, _) => OnConnectionLost(generation, $"engine process exited (code {SafeExitCode(process)})");

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, new JsonMessageFormatter()));
        rpc.AddLocalRpcTarget(new ClientTarget(this));
        rpc.Disconnected += (_, e) => OnConnectionLost(generation, $"connection lost ({e.Reason})");
        rpc.StartListening();

        // Sends wait for the LSP handshake only — not for resynchronisation, which itself sends (no self-wait).
        var initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _process = process;
        _rpc = rpc;
        _ready = initialized.Task;
        _ = Task.Run(() => InitializeAsync(rpc, generation, initialized));
        _log($"Engine started: pid {process.Id} ({process.StartInfo.FileName}).");
    }

    private async Task InitializeAsync(JsonRpc rpc, int generation, TaskCompletionSource<bool> initialized)
    {
        try
        {
            await rpc.InvokeWithParameterObjectAsync<JToken>("initialize", new { processId = Process.GetCurrentProcess().Id, rootUri = (string?)null, capabilities = new { } })
                .ConfigureAwait(false);
            await rpc.NotifyWithParameterObjectAsync("initialized", new { }).ConfigureAwait(false);
            initialized.TrySetResult(true);
        }
        catch (Exception exception) when (exception is ConnectionLostException or ObjectDisposedException or RemoteInvocationException)
        {
            _log($"Engine initialise failed: {exception.Message}");
            initialized.TrySetResult(false); // pending sends proceed and fail fast; the restart path resynchronises
            return;
        }

        List<OpenDocument> resync;
        lock (_gate)
        {
            if (generation != _generation || _disposed)
            {
                return;
            }

            resync = [.. _documents.Values];
            if (_downtime is not null)
            {
                _lastRestartMs = _downtime.Elapsed.TotalMilliseconds;
                _downtime = null;
            }
        }

        // After a restart the engine has no documents: re-send every open document with its current text.
        foreach (var document in resync)
        {
            if (document.OpenedInGeneration != generation)
            {
                await SendOpenAsync(document).ConfigureAwait(false);
            }
        }

        if (_lastRestartMs is { } ms && RestartCount > 0)
        {
            _log($"Engine ready after restart in {ms:0} ms; resynchronised {resync.Count} document(s).");
        }

        Ready?.Invoke(generation);
    }

    private void OnConnectionLost(int generation, string reason)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _process is null)
            {
                return; // already handled for this generation, or shutting down
            }

            _log($"Engine lost: {reason}.");
            _downtime = Stopwatch.StartNew();
            try
            {
                _rpc?.Dispose();
                if (_process is { HasExited: false })
                {
                    _process.Kill();
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            _process.Dispose();
            _process = null;
            _rpc = null;
            ScheduleRestartLocked(generation);
        }
    }

    private void ScheduleRestartLocked(int generation)
    {
        var now = DateTime.UtcNow;
        _restarts.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
        if (_restarts.Count >= MaxRestartsPerMinute)
        {
            _failed = true;
            _log($"Engine failed {MaxRestartsPerMinute} times within a minute; not restarting. Diagnostics are unavailable.");
            return;
        }

        _restarts.Add(now);
        var delay = TimeSpan.FromMilliseconds(200 * _restarts.Count);
        _ = Task.Delay(delay).ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    if (!_disposed && !_failed && _process is null && generation == _generation)
                    {
                        StartLocked();
                    }
                }
            },
            TaskScheduler.Default);
    }

    // ───────────────────────────── documents ─────────────────────────────

    private async Task SendOpenAsync(OpenDocument document)
    {
        int generation;
        lock (_gate)
        {
            generation = _generation;
        }

        var text = document.Capture();
        var version = document.Record(text.Tag);
        document.OpenedInGeneration = generation;
        await SendAsync(rpc => rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = document.Uri, languageId = "sql", version, text = text.Text },
        })).ConfigureAwait(false);
    }

    private async Task SendChangeAsync(OpenDocument document)
    {
        lock (_gate)
        {
            if (!_documents.ContainsKey(document.Uri))
            {
                return;
            }
        }

        var text = document.Capture();
        var version = document.Record(text.Tag);
        await SendAsync(rpc => rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
        {
            textDocument = new { uri = document.Uri, version },
            contentChanges = new[] { new { text = text.Text } },
        })).ConfigureAwait(false);
    }

    /// <summary>Sends once the engine is initialised; drops the message if the connection is down (resync covers it).</summary>
    private async Task SendAsync(Func<JsonRpc, Task> send)
    {
        JsonRpc? rpc;
        Task ready;
        lock (_gate)
        {
            (rpc, ready) = (_rpc, _ready);
        }

        if (rpc is null)
        {
            return;
        }

        try
        {
            await ready.ConfigureAwait(false);
            await send(rpc).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ConnectionLostException or ObjectDisposedException or System.IO.IOException)
        {
        }
    }

    private void OnPublishDiagnostics(JToken parameters)
    {
        var uri = parameters.Value<string>("uri") ?? string.Empty;
        var version = parameters.Value<int?>("version");
        var diagnostics = (parameters["diagnostics"] as JArray ?? new JArray())
            .Select(d => new EngineDiagnostic(
                d["range"]?["start"]?.Value<int>("line") ?? 0,
                d["range"]?["start"]?.Value<int>("character") ?? 0,
                d["range"]?["end"]?.Value<int>("line") ?? 0,
                d["range"]?["end"]?.Value<int>("character") ?? 0,
                d.Value<string>("code") ?? string.Empty,
                d.Value<string>("message") ?? string.Empty,
                d.Value<int?>("severity") ?? 1))
            .ToList();

        OpenDocument? document;
        lock (_gate)
        {
            _documents.TryGetValue(uri, out document);
        }

        if (document is null)
        {
            return; // closed meanwhile
        }

        object? tag = null;
        if (version is { } v)
        {
            if (!document.TryComplete(v, out tag, out var roundTripMs))
            {
                return; // superseded by a newer version already published
            }

            lock (_gate)
            {
                _roundTripsMs.Add(roundTripMs);
                if (_roundTripsMs.Count > 2000)
                {
                    _roundTripsMs.RemoveRange(0, 1000);
                }
            }
        }

        DiagnosticsPublished?.Invoke(new PublishedDiagnostics(uri, version, tag, diagnostics));
    }

    private double Percentile(double p)
    {
        var sorted = _roundTripsMs.OrderBy(x => x).ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private sealed class OpenDocument
    {
        private readonly object _gate = new();
        private readonly Func<DocumentText> _currentText;
        private readonly Dictionary<int, (object? Tag, long SentTimestamp)> _pending = new();
        private int _version;
        private int _lastPublished;

        public OpenDocument(string uri, Func<DocumentText> currentText)
        {
            Uri = uri;
            _currentText = currentText;
        }

        public string Uri { get; }

        public Timer? Debounce { get; set; }

        public int OpenedInGeneration { get; set; }

        public DocumentText Capture() => _currentText();

        public int Record(object? tag)
        {
            lock (_gate)
            {
                _version++;
                _pending[_version] = (tag, Stopwatch.GetTimestamp());
                return _version;
            }
        }

        /// <summary>Accepts results for <paramref name="version"/> unless a newer version was already published.</summary>
        public bool TryComplete(int version, out object? tag, out double roundTripMs)
        {
            lock (_gate)
            {
                tag = null;
                roundTripMs = 0;
                if (version <= _lastPublished || !_pending.TryGetValue(version, out var sent))
                {
                    return false;
                }

                _lastPublished = version;
                foreach (var stale in _pending.Keys.Where(k => k <= version).ToList())
                {
                    _pending.Remove(stale);
                }

                tag = sent.Tag;
                roundTripMs = (Stopwatch.GetTimestamp() - sent.SentTimestamp) * 1000.0 / Stopwatch.Frequency;
                return true;
            }
        }
    }

    /// <summary>Server → client notifications.</summary>
    private sealed class ClientTarget
    {
        private readonly EngineConnection _owner;

        public ClientTarget(EngineConnection owner) => _owner = owner;

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(JToken parameters) => _owner.OnPublishDiagnostics(parameters);
    }
}
