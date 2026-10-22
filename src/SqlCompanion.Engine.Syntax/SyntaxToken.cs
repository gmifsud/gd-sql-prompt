using System.Collections.Immutable;
using System.Text;

namespace SqlCompanion.Engine.Syntax;

/// <summary>Whitespace, a line break or a comment attached to a token.</summary>
public sealed record SyntaxTrivia(SyntaxKind Kind, int Position, string Text)
{
    public TextSpan Span => new(Position, Text.Length);
}

/// <summary>
/// A lexical token with its leading and trailing trivia. Concatenating
/// <see cref="ToFullString"/> over all tokens of a document reproduces the source exactly.
/// </summary>
/// <remarks>
/// Trivia ownership follows Roslyn: trailing trivia runs to and including the first line break;
/// everything after it is leading trivia of the next token.
/// </remarks>
public sealed class SyntaxToken
{
    public SyntaxToken(
        SyntaxKind kind,
        int position,
        string text,
        ImmutableArray<SyntaxTrivia> leadingTrivia,
        ImmutableArray<SyntaxTrivia> trailingTrivia)
    {
        Kind = kind;
        Position = position;
        Text = text;
        LeadingTrivia = leadingTrivia;
        TrailingTrivia = trailingTrivia;
    }

    public SyntaxKind Kind { get; }

    /// <summary>Offset of the token text, excluding leading trivia.</summary>
    public int Position { get; }

    public string Text { get; }

    public ImmutableArray<SyntaxTrivia> LeadingTrivia { get; }

    public ImmutableArray<SyntaxTrivia> TrailingTrivia { get; }

    public TextSpan Span => new(Position, Text.Length);

    public TextSpan FullSpan
    {
        get
        {
            var start = LeadingTrivia.IsEmpty ? Position : LeadingTrivia[0].Position;
            var end = TrailingTrivia.IsEmpty ? Span.End : TrailingTrivia[^1].Span.End;
            return TextSpan.FromBounds(start, end);
        }
    }

    public string ToFullString()
    {
        var builder = new StringBuilder(FullSpan.Length);
        foreach (var trivia in LeadingTrivia)
        {
            builder.Append(trivia.Text);
        }

        builder.Append(Text);
        foreach (var trivia in TrailingTrivia)
        {
            builder.Append(trivia.Text);
        }

        return builder.ToString();
    }

    public override string ToString() => $"{Kind} {Span} \"{Text}\"";
}

public sealed record LexResult(string Text, ImmutableArray<SyntaxToken> Tokens, ImmutableArray<Diagnostic> Diagnostics);
