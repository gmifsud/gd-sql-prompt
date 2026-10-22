using System.Collections.Immutable;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Binding;

/// <summary>
/// Phase 0 completion seed: reserved keywords plus variables seen in the document.
/// Not yet context-aware — Phase 2 replaces this with scope-graph-driven completion (CMP-001–005).
/// </summary>
/// <remarks>Completion is suppressed inside string literals, quoted identifiers and comments.</remarks>
public sealed class KeywordCompletionService : ICompletionService
{
    private static readonly ImmutableArray<CompletionItem> KeywordItems =
    [
        .. SyntaxFacts.ReservedKeywords
            .Order(StringComparer.Ordinal)
            .Select(k => new CompletionItem(k, CompletionItemKind.Keyword)),
    ];

    public ValueTask<CompletionResult> GetCompletionsAsync(DocumentSnapshot document, int position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();

        var lex = Lexer.Lex(document.Text);
        if (IsInsideOpaqueRegion(lex, position))
        {
            return ValueTask.FromResult(CompletionResult.Empty);
        }

        // Exclude the variable currently under the caret: it is the partial word being typed.
        var variables = lex.Tokens
            .Where(t => t.Kind == SyntaxKind.VariableToken && t.Text.Length > 1)
            .Where(t => position < t.Span.Start || position > t.Span.End)
            .Select(t => t.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(v => new CompletionItem(v, CompletionItemKind.Variable));

        return ValueTask.FromResult(new CompletionResult([.. variables, .. KeywordItems]));
    }

    /// <summary>
    /// True when <paramref name="position"/> lies inside a literal, quoted identifier or comment:
    /// strictly after its start and before its end, or at its end when it is unterminated
    /// (single-line comments are always open at their end).
    /// </summary>
    internal static bool IsInsideOpaqueRegion(LexResult lex, int position)
    {
        var unterminatedStarts = lex.Diagnostics
            .Where(d => d.Id is SyntaxDiagnosticIds.UnterminatedString
                or SyntaxDiagnosticIds.UnterminatedQuotedIdentifier
                or SyntaxDiagnosticIds.UnterminatedComment)
            .Select(d => d.Span.Start)
            .ToHashSet();

        foreach (var token in lex.Tokens)
        {
            if (token.FullSpan.Start > position)
            {
                break;
            }

            if (token.Kind is SyntaxKind.StringLiteralToken or SyntaxKind.QuotedIdentifierToken)
            {
                // N'...' literals: the delimiter (and any diagnostic) starts after the prefix.
                var delimiterStart = token.Text[0] is 'N' or 'n' ? token.Position + 1 : token.Position;
                if (IsInside(token.Span, position, unterminatedStarts.Contains(delimiterStart)))
                {
                    return true;
                }
            }

            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                var openAtEnd = trivia.Kind == SyntaxKind.SingleLineCommentTrivia || unterminatedStarts.Contains(trivia.Position);
                if (SyntaxFacts.IsComment(trivia.Kind) && IsInside(trivia.Span, position, openAtEnd))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsInside(TextSpan span, int position, bool openAtEnd) =>
        position > span.Start && (position < span.End || (openAtEnd && position == span.End));
}
