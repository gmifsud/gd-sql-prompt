using System.Collections.Immutable;

namespace SqlCompanion.Engine;

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

/// <summary>A finding with a stable rule identifier (e.g. <c>SQLC0001</c>) and a precise source range.</summary>
public sealed record Diagnostic(string Id, DiagnosticSeverity Severity, TextSpan Span, string Message);

public enum CompletionItemKind
{
    Keyword,
    Variable,
    Schema,
    Table,
    View,
    Column,
    Function,
    Procedure,
    Snippet,
}

public sealed record CompletionItem(string Label, CompletionItemKind Kind, string? Detail = null);

/// <param name="Items">Candidate completions.</param>
/// <param name="IsIncomplete">True when the host should re-query as the user keeps typing.</param>
/// <param name="IsStale">True when the results rely on cached metadata that may be out of date (CMP-014).</param>
public sealed record CompletionResult(ImmutableArray<CompletionItem> Items, bool IsIncomplete = false, bool IsStale = false)
{
    public static CompletionResult Empty { get; } = new([]);
}

/// <summary>
/// Engine service contracts. Every operation takes an immutable snapshot and a cancellation token
/// so hosts can abandon work superseded by newer edits.
/// </summary>
public interface IDiagnosticService
{
    ValueTask<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(DocumentSnapshot document, CancellationToken cancellationToken);
}

public interface IFormattingService
{
    /// <summary>Returns edits that format the document, or the given range when supplied (FMT-001).</summary>
    ValueTask<ImmutableArray<TextEdit>> FormatAsync(DocumentSnapshot document, TextSpan? range, CancellationToken cancellationToken);
}

public interface ICompletionService
{
    ValueTask<CompletionResult> GetCompletionsAsync(DocumentSnapshot document, int position, CancellationToken cancellationToken);
}
