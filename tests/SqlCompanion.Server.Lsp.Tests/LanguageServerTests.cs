using System.Text.Json;
using System.Threading.Channels;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace SqlCompanion.Server.Lsp.Tests;

/// <summary>
/// End-to-end Phase 0 slice: an LSP client drives the server over an in-memory duplex stream
/// through initialise → open/change → diagnostics → completion → formatting → shutdown/exit.
/// </summary>
public sealed class LanguageServerTests : IAsyncDisposable
{
    private const string Uri = "file:///workspace/query.sql";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly JsonRpc _client;
    private readonly JsonRpc _serverRpc;
    private readonly LanguageServer _server;
    private readonly DiagnosticsSink _sink = new();

    public LanguageServerTests()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        (_serverRpc, _server) = LanguageServerHost.Start(serverStream, serverStream);

        _client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, LanguageServerHost.CreateFormatter()));
        _client.AddLocalRpcTarget(_sink);
        _client.StartListening();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Initialize_AdvertisesPhase0Capabilities()
    {
        var result = await InitializeAsync();
        var capabilities = result.GetProperty("capabilities");

        Assert.Equal(1, capabilities.GetProperty("textDocumentSync").GetInt32());
        Assert.True(capabilities.GetProperty("documentFormattingProvider").GetBoolean());
        Assert.Contains("@", capabilities.GetProperty("completionProvider").GetProperty("triggerCharacters").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task OpenAndChange_PublishVersionedDiagnostics()
    {
        await InitializeAsync();

        await OpenAsync("SELECT 'oops", version: 1);
        var first = await _sink.NextAsync(Ct);
        Assert.Equal(1, first.Version);
        Assert.Equal("SQLC0001", Assert.Single(first.Diagnostics).Code);
        Assert.Equal(new LspRange(new LspPosition(0, 7), new LspPosition(0, 12)), first.Diagnostics[0].Range);

        await _client.NotifyWithParameterObjectAsync("textDocument/didChange", new
        {
            textDocument = new { uri = Uri, version = 2 },
            contentChanges = new[] { new { text = "SELECT 'fixed'" } },
        });
        var second = await _sink.NextAsync(Ct);
        Assert.Equal(2, second.Version);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public async Task Completion_ReturnsKeywordsAtCaret()
    {
        await InitializeAsync();
        await OpenAsync("SEL", version: 1);
        await _sink.NextAsync(Ct);

        var list = await _client.InvokeWithParameterObjectAsync<LspCompletionList>(
            "textDocument/completion",
            new { textDocument = new { uri = Uri }, position = new { line = 0, character = 3 } },
            Ct);

        Assert.Contains(list.Items, i => i.Label == "SELECT" && i.Kind == 14);
    }

    [Fact]
    public async Task Formatting_ReturnsNoEditsFromPlaceholderFormatter()
    {
        await InitializeAsync();
        await OpenAsync("select 1", version: 1);
        await _sink.NextAsync(Ct);

        var edits = await _client.InvokeWithParameterObjectAsync<LspTextEdit[]>(
            "textDocument/formatting",
            new { textDocument = new { uri = Uri }, options = new { tabSize = 4, insertSpaces = true } },
            Ct);

        Assert.Empty(edits);
    }

    [Fact]
    public async Task ShutdownThenExit_CompletesWithZeroExitCode()
    {
        await InitializeAsync();
        await _client.InvokeWithCancellationAsync<object?>("shutdown", arguments: null, Ct);
        await _client.NotifyAsync("exit");

        Assert.Equal(0, await _server.ExitCode.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task ExitWithoutShutdown_CompletesWithNonZeroExitCode()
    {
        await InitializeAsync();
        await _client.NotifyAsync("exit");

        Assert.Equal(1, await _server.ExitCode.WaitAsync(Timeout, Ct));
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _serverRpc.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<JsonElement> InitializeAsync() =>
        _client.InvokeWithParameterObjectAsync<JsonElement>("initialize", new { processId = (int?)null, capabilities = new { } }, Ct);

    private Task OpenAsync(string text, int version) =>
        _client.NotifyWithParameterObjectAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = Uri, languageId = "sql", version, text },
        });

    private sealed class DiagnosticsSink
    {
        private readonly Channel<PublishDiagnosticsParams> _received = Channel.CreateUnbounded<PublishDiagnosticsParams>();

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void Publish(PublishDiagnosticsParams parameters) => _received.Writer.TryWrite(parameters);

        public async Task<PublishDiagnosticsParams> NextAsync(CancellationToken cancellationToken) =>
            await _received.Reader.ReadAsync(cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken);
    }
}
