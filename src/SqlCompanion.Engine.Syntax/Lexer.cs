using System.Collections.Immutable;
using System.Text;

namespace SqlCompanion.Engine.Syntax;

/// <summary>
/// Lossless, error-tolerant T-SQL lexer (ADR-0002).
/// </summary>
/// <remarks>
/// Invariants:
/// <list type="number">
/// <item>Never throws on any input string.</item>
/// <item>Concatenating every token's <see cref="SyntaxElement.ToFullString"/> reproduces the input exactly.</item>
/// <item>The final token is always <see cref="SyntaxKind.EndOfFileToken"/>, which owns trailing trivia of the document.</item>
/// </list>
/// Malformed input (unterminated strings, comments or quoted identifiers; stray characters) yields a
/// token plus a diagnostic rather than an exception.
/// </remarks>
public sealed class Lexer
{
    private readonly string _text;
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
    private int _position;

    private Lexer(string text) => _text = text;

    public static LexResult Lex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lexer = new Lexer(text);
        var tokens = lexer.LexAll();
        return new LexResult(text, tokens, lexer._diagnostics.ToImmutable());
    }

    private char Current => Peek(0);

    private char Peek(int offset)
    {
        var index = _position + offset;
        return index < _text.Length ? _text[index] : '\0';
    }

    private bool AtEnd => _position >= _text.Length;

    private ImmutableArray<SyntaxToken> LexAll()
    {
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>();
        while (true)
        {
            var leading = ReadTrivia(isTrailing: false);
            var start = _position;
            var kind = ReadToken();
            var text = _text[start.._position];

            if (kind == SyntaxKind.EndOfFileToken)
            {
                tokens.Add(new SyntaxToken(kind, start, text, leading, []));
                return tokens.ToImmutable();
            }

            var trailing = ReadTrivia(isTrailing: true);
            tokens.Add(new SyntaxToken(kind, start, text, leading, trailing));
        }
    }

    private ImmutableArray<SyntaxTrivia> ReadTrivia(bool isTrailing)
    {
        var trivia = ImmutableArray.CreateBuilder<SyntaxTrivia>();
        while (!AtEnd)
        {
            var start = _position;
            SyntaxKind kind;

            switch (Current)
            {
                case '\r':
                    _position += Peek(1) == '\n' ? 2 : 1;
                    kind = SyntaxKind.EndOfLineTrivia;
                    break;
                case '\n':
                    _position++;
                    kind = SyntaxKind.EndOfLineTrivia;
                    break;
                case '-' when Peek(1) == '-':
                    while (!AtEnd && Current is not ('\r' or '\n'))
                    {
                        _position++;
                    }

                    kind = SyntaxKind.SingleLineCommentTrivia;
                    break;
                case '/' when Peek(1) == '*':
                    ReadBlockComment();
                    kind = SyntaxKind.MultiLineCommentTrivia;
                    break;
                case var c when char.IsWhiteSpace(c):
                    while (!AtEnd && char.IsWhiteSpace(Current) && Current is not ('\r' or '\n'))
                    {
                        _position++;
                    }

                    kind = SyntaxKind.WhitespaceTrivia;
                    break;
                default:
                    return trivia.ToImmutable();
            }

            trivia.Add(new SyntaxTrivia(kind, start, _text[start.._position]));

            if (isTrailing && kind == SyntaxKind.EndOfLineTrivia)
            {
                break;
            }
        }

        return trivia.ToImmutable();
    }

    /// <summary>T-SQL block comments nest: <c>/* a /* b */ c */</c> is a single comment.</summary>
    private void ReadBlockComment()
    {
        var start = _position;
        _position += 2;
        var depth = 1;

        while (!AtEnd)
        {
            if (Current == '/' && Peek(1) == '*')
            {
                depth++;
                _position += 2;
            }
            else if (Current == '*' && Peek(1) == '/')
            {
                depth--;
                _position += 2;
                if (depth == 0)
                {
                    return;
                }
            }
            else
            {
                _position++;
            }
        }

        Report(SyntaxDiagnosticIds.UnterminatedComment, start, "Unterminated block comment.");
    }

    private SyntaxKind ReadToken()
    {
        if (AtEnd)
        {
            return SyntaxKind.EndOfFileToken;
        }

        var c = Current;
        switch (c)
        {
            case '\'':
                ReadDelimited('\'', SyntaxDiagnosticIds.UnterminatedString, "Unterminated string literal.");
                return SyntaxKind.StringLiteralToken;
            case 'N' or 'n' when Peek(1) == '\'':
                _position++;
                ReadDelimited('\'', SyntaxDiagnosticIds.UnterminatedString, "Unterminated string literal.");
                return SyntaxKind.StringLiteralToken;
            case '[':
                ReadDelimited(']', SyntaxDiagnosticIds.UnterminatedQuotedIdentifier, "Unterminated bracketed identifier.");
                return SyntaxKind.QuotedIdentifierToken;
            case '"':
                ReadDelimited('"', SyntaxDiagnosticIds.UnterminatedQuotedIdentifier, "Unterminated quoted identifier.");
                return SyntaxKind.QuotedIdentifierToken;
            case '0' when Peek(1) is 'x' or 'X':
                _position += 2;
                while (char.IsAsciiHexDigit(Current))
                {
                    _position++;
                }

                return SyntaxKind.BinaryLiteralToken;
            case var _ when char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(1))):
                ReadNumber();
                return SyntaxKind.NumericLiteralToken;
            case '$' when char.IsAsciiDigit(Peek(1)) || (Peek(1) == '.' && char.IsAsciiDigit(Peek(2))):
                _position++;
                ReadNumber();
                return SyntaxKind.NumericLiteralToken;
            case '@':
                _position++;
                ReadIdentifierTail();
                return SyntaxKind.VariableToken;
            case '$' when char.IsAsciiLetter(Peek(1)):
                // Pseudo-columns: $action (MERGE OUTPUT), $IDENTITY, $ROWGUID, $node_id, ...
                _position++;
                ReadIdentifierTail();
                return SyntaxKind.IdentifierToken;
        }

        if (IsIdentifierStart())
        {
            var start = _position;
            AdvanceRune();
            ReadIdentifierTail();
            return SyntaxFacts.IsReservedKeyword(_text[start.._position])
                ? SyntaxKind.KeywordToken
                : SyntaxKind.IdentifierToken;
        }

        return ReadPunctuation();
    }

    /// <summary>Reads a delimited run where a doubled closing delimiter is an escape (<c>''</c>, <c>]]</c>, <c>""</c>).</summary>
    private void ReadDelimited(char close, string diagnosticId, string message)
    {
        var start = _position;
        _position++;

        while (!AtEnd)
        {
            if (Current == close)
            {
                if (Peek(1) == close)
                {
                    _position += 2;
                    continue;
                }

                _position++;
                return;
            }

            _position++;
        }

        Report(diagnosticId, start, message);
    }

    private void ReadNumber()
    {
        while (char.IsAsciiDigit(Current))
        {
            _position++;
        }

        if (Current == '.')
        {
            _position++;
            while (char.IsAsciiDigit(Current))
            {
                _position++;
            }
        }

        // Exponent is consumed only when well-formed; "1e" otherwise lexes as 1 followed by identifier "e".
        if (Current is 'e' or 'E')
        {
            var signLength = Peek(1) is '+' or '-' ? 1 : 0;
            if (char.IsAsciiDigit(Peek(1 + signLength)))
            {
                _position += 1 + signLength;
                while (char.IsAsciiDigit(Current))
                {
                    _position++;
                }
            }
        }
    }

    private bool IsIdentifierStart()
    {
        if (Current is '_' or '#')
        {
            return true;
        }

        return Rune.TryGetRuneAt(_text, _position, out var rune) && Rune.IsLetter(rune);
    }

    private void ReadIdentifierTail()
    {
        while (!AtEnd)
        {
            if (Current is '_' or '@' or '#' or '$')
            {
                _position++;
            }
            else if (Rune.TryGetRuneAt(_text, _position, out var rune) && Rune.IsLetterOrDigit(rune))
            {
                _position += rune.Utf16SequenceLength;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Advances one scalar value, keeping surrogate pairs (and lone surrogates) intact.</summary>
    private void AdvanceRune()
    {
        _position += char.IsHighSurrogate(Current) && char.IsLowSurrogate(Peek(1)) ? 2 : 1;
    }

    private SyntaxKind ReadPunctuation()
    {
        var c = Current;
        var next = Peek(1);

        (SyntaxKind Kind, int Length) match = c switch
        {
            '(' => (SyntaxKind.OpenParenToken, 1),
            ')' => (SyntaxKind.CloseParenToken, 1),
            '{' => (SyntaxKind.OpenBraceToken, 1),
            '}' => (SyntaxKind.CloseBraceToken, 1),
            ',' => (SyntaxKind.CommaToken, 1),
            ';' => (SyntaxKind.SemicolonToken, 1),
            '.' => (SyntaxKind.DotToken, 1),
            ':' when next == ':' => (SyntaxKind.ColonColonToken, 2),
            ':' => (SyntaxKind.ColonToken, 1),
            '+' when next == '=' => (SyntaxKind.PlusEqualsToken, 2),
            '+' => (SyntaxKind.PlusToken, 1),
            '-' when next == '=' => (SyntaxKind.MinusEqualsToken, 2),
            '-' => (SyntaxKind.MinusToken, 1),
            '*' when next == '=' => (SyntaxKind.AsteriskEqualsToken, 2),
            '*' => (SyntaxKind.AsteriskToken, 1),
            '/' when next == '=' => (SyntaxKind.SlashEqualsToken, 2),
            '/' => (SyntaxKind.SlashToken, 1),
            '%' when next == '=' => (SyntaxKind.PercentEqualsToken, 2),
            '%' => (SyntaxKind.PercentToken, 1),
            '&' when next == '=' => (SyntaxKind.AmpersandEqualsToken, 2),
            '&' => (SyntaxKind.AmpersandToken, 1),
            '|' when next == '|' && Peek(2) == '=' => (SyntaxKind.BarBarEqualsToken, 3),
            '|' when next == '|' => (SyntaxKind.BarBarToken, 2),
            '|' when next == '=' => (SyntaxKind.BarEqualsToken, 2),
            '|' => (SyntaxKind.BarToken, 1),
            '^' when next == '=' => (SyntaxKind.CaretEqualsToken, 2),
            '^' => (SyntaxKind.CaretToken, 1),
            '~' => (SyntaxKind.TildeToken, 1),
            '=' => (SyntaxKind.EqualsToken, 1),
            '<' when next == '=' => (SyntaxKind.LessThanEqualsToken, 2),
            '<' when next == '>' => (SyntaxKind.NotEqualsToken, 2),
            '<' => (SyntaxKind.LessThanToken, 1),
            '>' when next == '=' => (SyntaxKind.GreaterThanEqualsToken, 2),
            '>' => (SyntaxKind.GreaterThanToken, 1),
            '!' when next == '=' => (SyntaxKind.NotEqualsToken, 2),
            '!' when next == '<' => (SyntaxKind.NotLessThanToken, 2),
            '!' when next == '>' => (SyntaxKind.NotGreaterThanToken, 2),
            _ => (SyntaxKind.BadToken, 0),
        };

        if (match.Kind != SyntaxKind.BadToken)
        {
            _position += match.Length;
            return match.Kind;
        }

        var start = _position;
        AdvanceRune();
        Report(SyntaxDiagnosticIds.UnexpectedCharacter, start, $"Unexpected character '{_text[start.._position]}'.");
        return SyntaxKind.BadToken;
    }

    private void Report(string id, int start, string message) =>
        _diagnostics.Add(new Diagnostic(id, DiagnosticSeverity.Error, TextSpan.FromBounds(start, _position), message));
}
