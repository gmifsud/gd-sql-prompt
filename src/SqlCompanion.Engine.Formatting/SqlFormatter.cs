using System.Collections.Immutable;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Formatting;

/// <summary>Outcome of formatting a document.</summary>
/// <param name="Text">The formatted text, or the original text when <paramref name="SkippedReason"/> is set.</param>
/// <param name="Changed">Whether <paramref name="Text"/> differs from the input.</param>
/// <param name="SkippedReason">Set when the safety check rejected the layout; the input is returned unchanged.</param>
public sealed record FormatResult(string Text, bool Changed, string? SkippedReason);

/// <summary>
/// T-SQL formatter (FMT-001…004, 006, 008, 009). Formatting only changes whitespace between tokens and keyword
/// casing. Every result is re-parsed and checked against the input — same tokens, same comments, same statement
/// and batch structure — and a layout that fails the check is discarded, never applied.
/// </summary>
public static class SqlFormatter
{
    public static FormatResult Format(string text, StyleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(profile);
        var tree = SyntaxTree.Parse(text);
        var formatted = Layout(tree, profile);
        var problem = Verify(tree, SyntaxTree.Parse(formatted));
        return problem is null
            ? new FormatResult(formatted, !string.Equals(formatted, text, StringComparison.Ordinal), null)
            : new FormatResult(text, false, problem);
    }

    /// <summary>
    /// Minimal edits turning <paramref name="text"/> into its formatted form: one per changed run of whitespace
    /// before a token or comment, and one per re-cased keyword. With <paramref name="range"/>, only edits owned by
    /// tokens and comments on the lines the range touches.
    /// </summary>
    public static ImmutableArray<TextEdit> ComputeEdits(string text, StyleProfile profile, TextSpan? range = null)
    {
        var result = Format(text, profile);
        if (!result.Changed)
        {
            return [];
        }

        var before = Anchors(SyntaxTree.Parse(text));
        var after = Anchors(SyntaxTree.Parse(result.Text));
        if (before.Count != after.Count)
        {
            return []; // unreachable after a passed verification; never emit a mismatched edit set
        }

        var (rangeStart, rangeEnd) = range is { } r ? ExpandToLines(text, r) : (0, text.Length);
        var edits = ImmutableArray.CreateBuilder<TextEdit>();
        var previousEnd = 0;
        var previousEndAfter = 0;
        for (var i = 0; i < before.Count; i++)
        {
            var (start, end) = before[i];
            var (startAfter, endAfter) = after[i];
            var owned = start >= rangeStart && start <= rangeEnd;
            var gapAfter = result.Text[previousEndAfter..startAfter];
            var anchorAfter = result.Text[startAfter..endAfter];
            var gapChanged = !text.AsSpan(previousEnd, start - previousEnd).SequenceEqual(gapAfter);
            var anchorChanged = !text.AsSpan(start, end - start).SequenceEqual(anchorAfter);
            if (owned && (gapChanged || anchorChanged))
            {
                // One edit per anchor, so no two edits share a position (an insert next to a replace is ambiguous).
                edits.Add(gapChanged && anchorChanged ? new TextEdit(TextSpan.FromBounds(previousEnd, end), gapAfter + anchorAfter)
                    : gapChanged ? new TextEdit(TextSpan.FromBounds(previousEnd, start), gapAfter)
                    : new TextEdit(TextSpan.FromBounds(start, end), anchorAfter));
            }

            previousEnd = end;
            previousEndAfter = endAfter;
        }

        return edits.ToImmutable();
    }

    private static string Layout(SyntaxTree tree, StyleProfile profile)
    {
        var newLine = profile.LineEndings switch
        {
            LineEndingPolicy.CrLf => "\r\n",
            LineEndingPolicy.Lf => "\n",
            _ => PredominantLineEnding(tree.Text),
        };

        var printed = DocPrinter.Print(LayoutBuilder.Build(tree, profile), profile.MaxLineWidth, profile.IndentUnit, newLine);
        printed = printed.TrimEnd('\r', '\n'); // the printer's own breaks; comments and literals never end in one
        return EndsWithLineBreak(tree.Text) && printed.Length > 0 ? printed + newLine : printed;
    }

    /// <summary>Null when <paramref name="formatted"/> is equivalent to <paramref name="original"/>, else the first difference.</summary>
    internal static string? Verify(SyntaxTree original, SyntaxTree formatted)
    {
        var a = original.Root.DescendantTokens().ToList();
        var b = formatted.Root.DescendantTokens().ToList();
        if (a.Count != b.Count)
        {
            return $"token count changed ({a.Count} → {b.Count})";
        }

        for (var i = 0; i < a.Count; i++)
        {
            var same = a[i].Kind == b[i].Kind && (string.Equals(a[i].Text, b[i].Text, StringComparison.Ordinal)
                || a[i].Kind == SyntaxKind.KeywordToken && string.Equals(a[i].Text, b[i].Text, StringComparison.OrdinalIgnoreCase));
            if (!same)
            {
                return $"token {i} changed ('{a[i].Text}' → '{b[i].Text}')";
            }
        }

        if (!Comments(a).SequenceEqual(Comments(b), StringComparer.Ordinal))
        {
            return "comments changed";
        }

        if (!Structure(original.Root, a).SequenceEqual(Structure(formatted.Root, b)))
        {
            return "statement or batch structure changed";
        }

        return null;
    }

    private static IEnumerable<string> Comments(List<SyntaxToken> tokens) =>
        tokens.SelectMany(t => t.LeadingTrivia.Concat(t.TrailingTrivia))
            .Where(t => SyntaxFacts.IsComment(t.Kind))
            .Select(t => t.Text);

    /// <summary>Statement and batch boundaries as (kind, index of first token): line breaks can move GO and statement starts.</summary>
    private static List<(SyntaxKind Kind, int FirstToken)> Structure(SyntaxNode root, List<SyntaxToken> tokens)
    {
        var index = new Dictionary<SyntaxToken, int>(tokens.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < tokens.Count; i++)
        {
            index[tokens[i]] = i;
        }

        return root.DescendantNodes()
            .Where(n => n.Kind is SyntaxKind.Batch or SyntaxKind.BatchSeparator or SyntaxKind.ErrorNode
                || n.Kind.ToString().EndsWith("Statement", StringComparison.Ordinal))
            .Select(n => (n.Kind, index[n.FirstToken]))
            .ToList();
    }

    /// <summary>Spans of every token and comment, in order; between consecutive anchors there is only whitespace.</summary>
    private static List<(int Start, int End)> Anchors(SyntaxTree tree)
    {
        var anchors = new List<(int, int)>();
        foreach (var token in tree.Root.DescendantTokens())
        {
            foreach (var trivia in token.LeadingTrivia.Where(t => SyntaxFacts.IsComment(t.Kind)))
            {
                anchors.Add((trivia.Position, trivia.Span.End));
            }

            anchors.Add((token.Position, token.Span.End));
            foreach (var trivia in token.TrailingTrivia.Where(t => SyntaxFacts.IsComment(t.Kind)))
            {
                anchors.Add((trivia.Position, trivia.Span.End));
            }
        }

        return anchors;
    }

    private static (int Start, int End) ExpandToLines(string text, TextSpan range)
    {
        var start = Math.Clamp(range.Start, 0, text.Length);
        var end = Math.Clamp(range.End, start, text.Length);
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = text.IndexOf('\n', end == start ? end : end - 1);
        return (lineStart, lineEnd < 0 ? text.Length : lineEnd);
    }

    private static string PredominantLineEnding(string text)
    {
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r')
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }
        }

        return lf > crlf ? "\n" : "\r\n";
    }

    private static bool EndsWithLineBreak(string text) => text.EndsWith('\n');
}
