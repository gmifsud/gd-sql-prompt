using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SqlCompanion.Engine;
using StreamJsonRpc;

namespace SqlCompanion.Server.Lsp;

/// <summary>
/// LSP request and notification handlers. Engine services are injected so every host exercises
/// the same implementation (ADR-0001).
/// </summary>
public sealed class LanguageServer(
    ICompletionService completion,
    IFormattingService formatting,
    IDiagnosticService diagnostics)
{
    private const string DiagnosticSource = "sqlwc";

    private readonly DocumentStore _documents = new();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _shutdownRequested;

    /// <summary>Set by the host after the JSON-RPC connection is created; used for server→client notifications.</summary>
    public JsonRpc? Client { get; set; }

    /// <summary>Completes with the process exit code once the client sends <c>exit</c>.</summary>
    public Task<int> ExitCode => _exit.Task;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "JSON-RPC target methods are discovered as instance members.")]
    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public object Initialize(JsonElement parameters) => new
    {
        capabilities = new
        {
            textDocumentSync = TextDocumentSyncKind.Full,
            completionProvider = new { triggerCharacters = new[] { "@", "." } },
            documentFormattingProvider = true,
        },
        serverInfo = new { name = "sqlwc-lsp", version = typeof(LanguageServer).Assembly.GetName().Version?.ToString() },
    };

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "JSON-RPC target methods are discovered as instance members.")]
    [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)]
    public void Initialized(JsonElement parameters)
    {
    }

    [JsonRpcMethod("shutdown")]
    public object? Shutdown()
    {
        _shutdownRequested = true;
        return null;
    }

    [JsonRpcMethod("exit")]
    public void Exit() => _exit.TrySetResult(_shutdownRequested ? 0 : 1);

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public Task DidOpenAsync(DidOpenTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        var document = parameters.TextDocument;
        return UpdateAndPublishAsync(new DocumentSnapshot(document.Uri, document.Version, document.Text), cancellationToken);
    }

    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public Task DidChangeAsync(DidChangeTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        // Full sync: the last change event carries the complete document text.
        if (parameters.ContentChanges.Count == 0)
        {
            return Task.CompletedTask;
        }

        var document = parameters.TextDocument;
        return UpdateAndPublishAsync(new DocumentSnapshot(document.Uri, document.Version, parameters.ContentChanges[^1].Text), cancellationToken);
    }

    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public Task DidCloseAsync(DidCloseTextDocumentParams parameters, CancellationToken cancellationToken)
    {
        _documents.Remove(parameters.TextDocument.Uri);
        return PublishAsync(new PublishDiagnosticsParams(parameters.TextDocument.Uri, null, []), cancellationToken);
    }

    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public async Task<LspCompletionList> CompletionAsync(CompletionParams parameters, CancellationToken cancellationToken)
    {
        if (!_documents.TryGet(parameters.TextDocument.Uri, out var snapshot))
        {
            return new LspCompletionList(false, []);
        }

        var offset = new LineMap(snapshot.Text).GetOffset(ToLinePosition(parameters.Position));
        var result = await completion.GetCompletionsAsync(snapshot, offset, cancellationToken).ConfigureAwait(false);

        return new LspCompletionList(
            result.IsIncomplete,
            [.. result.Items.Select(i => new LspCompletionItem(i.Label, ToLspKind(i.Kind), i.Detail))]);
    }

    [JsonRpcMethod("textDocument/formatting", UseSingleObjectParameterDeserialization = true)]
    public async Task<IReadOnlyList<LspTextEdit>> FormattingAsync(DocumentFormattingParams parameters, CancellationToken cancellationToken)
    {
        if (!_documents.TryGet(parameters.TextDocument.Uri, out var snapshot))
        {
            return [];
        }

        var edits = await formatting.FormatAsync(snapshot, range: null, cancellationToken).ConfigureAwait(false);
        var lines = new LineMap(snapshot.Text);
        return [.. edits.Select(e => new LspTextEdit(ToLspRange(lines, e.Span), e.NewText))];
    }

    private async Task UpdateAndPublishAsync(DocumentSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_documents.TryUpdate(snapshot))
        {
            return;
        }

        var found = await diagnostics.GetDiagnosticsAsync(snapshot, cancellationToken).ConfigureAwait(false);

        // A newer version may have arrived while diagnostics were computed; never publish stale results.
        if (!_documents.IsCurrent(snapshot))
        {
            return;
        }

        var lines = new LineMap(snapshot.Text);
        await PublishAsync(new PublishDiagnosticsParams(snapshot.Uri, snapshot.Version, ToLspDiagnostics(lines, found)), cancellationToken)
            .ConfigureAwait(false);
    }

    private Task PublishAsync(PublishDiagnosticsParams parameters, CancellationToken cancellationToken) =>
        Client is null
            ? Task.CompletedTask
            : Client.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", parameters);

    private static LspDiagnostic[] ToLspDiagnostics(LineMap lines, ImmutableArray<Diagnostic> found) =>
        [.. found.Select(d => new LspDiagnostic(ToLspRange(lines, d.Span), (int)d.Severity, d.Id, DiagnosticSource, d.Message))];

    private static LinePosition ToLinePosition(LspPosition position) => new(position.Line, position.Character);

    private static LspRange ToLspRange(LineMap lines, TextSpan span)
    {
        var start = lines.GetPosition(span.Start);
        var end = lines.GetPosition(span.End);
        return new LspRange(new LspPosition(start.Line, start.Character), new LspPosition(end.Line, end.Character));
    }

    private static int ToLspKind(CompletionItemKind kind) => kind switch
    {
        CompletionItemKind.Keyword => LspCompletionItemKind.Keyword,
        CompletionItemKind.Variable => LspCompletionItemKind.Variable,
        CompletionItemKind.Schema => LspCompletionItemKind.Module,
        CompletionItemKind.Table => LspCompletionItemKind.Struct,
        CompletionItemKind.View => LspCompletionItemKind.Class,
        CompletionItemKind.Column => LspCompletionItemKind.Field,
        CompletionItemKind.Function => LspCompletionItemKind.Function,
        CompletionItemKind.Procedure => LspCompletionItemKind.Method,
        CompletionItemKind.Snippet => LspCompletionItemKind.Snippet,
        _ => LspCompletionItemKind.Keyword,
    };
}
