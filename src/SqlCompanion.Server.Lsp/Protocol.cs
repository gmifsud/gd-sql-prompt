namespace SqlCompanion.Server.Lsp;

// Minimal LSP 3.17 wire types for the Phase 0 slice. Serialised with camelCase naming.

public sealed record LspPosition(int Line, int Character);

public sealed record LspRange(LspPosition Start, LspPosition End);

public sealed record TextDocumentIdentifier(string Uri);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

public sealed record TextDocumentContentChangeEvent(string Text);

public sealed record DidChangeTextDocumentParams(
    VersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record CompletionParams(TextDocumentIdentifier TextDocument, LspPosition Position);

public sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument);

public sealed record LspTextEdit(LspRange Range, string NewText);

public sealed record LspCompletionItem(string Label, int Kind, string? Detail);

public sealed record LspCompletionList(bool IsIncomplete, IReadOnlyList<LspCompletionItem> Items);

public sealed record LspDiagnostic(LspRange Range, int Severity, string Code, string Source, string Message);

public sealed record PublishDiagnosticsParams(string Uri, int? Version, IReadOnlyList<LspDiagnostic> Diagnostics);

/// <summary>LSP <c>CompletionItemKind</c> values.</summary>
internal static class LspCompletionItemKind
{
    public const int Function = 3;
    public const int Field = 5;
    public const int Variable = 6;
    public const int Class = 7;
    public const int Module = 9;
    public const int Keyword = 14;
    public const int Snippet = 15;
    public const int Struct = 22;
    public const int Method = 2;
}

/// <summary>LSP <c>TextDocumentSyncKind</c> values.</summary>
internal static class TextDocumentSyncKind
{
    public const int Full = 1;
}
