using System.Text;
using CsCheck;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Tests;

public sealed class LexerTests
{
    /// <summary>Characters that exercise every lexer branch, including delimiters that open unterminated runs.</summary>
    private const string SqlAlphabet = "abcNnXxeE_@#$019.,;:()[]{}\"'-/*+%&|^~=<>! \t\r\nñ\U0001F600";

    private static readonly Gen<string> SqlLikeStrings =
        Gen.Char[SqlAlphabet].Array[0, 200].Select(chars => new string(chars));

    public static TheoryData<string> AllCorpusFiles => Corpus.Files();

    public static TheoryData<string> CleanCorpusFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var folder in new[] { "acceptance", "lexical" })
            {
                foreach (var row in Corpus.Files(folder))
                {
                    data.Add(row.Data);
                }
            }

            return data;
        }
    }

    [Fact]
    public void RoundTrip_HoldsForSqlLikeInput() =>
        SqlLikeStrings.Sample(AssertLosslessAndContiguous, iter: 20_000);

    [Fact]
    public void RoundTrip_HoldsForArbitraryUtf16IncludingLoneSurrogates() =>
        Gen.String.Sample(AssertLosslessAndContiguous, iter: 20_000);

    [Theory]
    [MemberData(nameof(AllCorpusFiles))]
    public void RoundTrip_HoldsForCorpus(string file) => AssertLosslessAndContiguous(Corpus.Read(file));

    [Theory]
    [MemberData(nameof(CleanCorpusFiles))]
    public void CleanCorpus_HasNoLexicalDiagnostics(string file) =>
        Assert.Empty(Lexer.Lex(Corpus.Read(file)).Diagnostics);

    [Theory]
    [InlineData("incomplete/unterminated-string.sql", SyntaxDiagnosticIds.UnterminatedString)]
    [InlineData("incomplete/unterminated-comment.sql", SyntaxDiagnosticIds.UnterminatedComment)]
    [InlineData("incomplete/unterminated-identifier.sql", SyntaxDiagnosticIds.UnterminatedQuotedIdentifier)]
    public void IncompleteCorpus_ReportsExpectedDiagnostic(string file, string expectedId)
    {
        var diagnostic = Assert.Single(Lexer.Lex(Corpus.Read(file)).Diagnostics);
        Assert.Equal(expectedId, diagnostic.Id);
    }

    [Theory]
    [InlineData("'it''s'", SyntaxKind.StringLiteralToken)]
    [InlineData("N'x'", SyntaxKind.StringLiteralToken)]
    [InlineData("[a]]b]", SyntaxKind.QuotedIdentifierToken)]
    [InlineData("\"a\"\"b\"", SyntaxKind.QuotedIdentifierToken)]
    [InlineData("@v", SyntaxKind.VariableToken)]
    [InlineData("@@ROWCOUNT", SyntaxKind.VariableToken)]
    [InlineData("#temp", SyntaxKind.IdentifierToken)]
    [InlineData("$action", SyntaxKind.IdentifierToken)]
    [InlineData("select", SyntaxKind.KeywordToken)]
    [InlineData("NOCOUNT", SyntaxKind.IdentifierToken)]
    [InlineData("0x", SyntaxKind.BinaryLiteralToken)]
    [InlineData("0xFF", SyntaxKind.BinaryLiteralToken)]
    [InlineData(".5", SyntaxKind.NumericLiteralToken)]
    [InlineData("2.5E-3", SyntaxKind.NumericLiteralToken)]
    [InlineData("$12.50", SyntaxKind.NumericLiteralToken)]
    [InlineData("<>", SyntaxKind.NotEqualsToken)]
    [InlineData("!=", SyntaxKind.NotEqualsToken)]
    [InlineData("!<", SyntaxKind.NotLessThanToken)]
    [InlineData("||=", SyntaxKind.BarBarEqualsToken)]
    [InlineData("::", SyntaxKind.ColonColonToken)]
    [InlineData("ñame", SyntaxKind.IdentifierToken)]
    public void SingleToken_HasExpectedKind(string text, SyntaxKind expected)
    {
        var tokens = Lexer.Lex(text).Tokens;
        Assert.Equal(2, tokens.Length);
        Assert.Equal(expected, tokens[0].Kind);
        Assert.Equal(text, tokens[0].Text);
    }

    [Fact]
    public void IncompleteExponent_LexesAsNumberThenIdentifier()
    {
        var kinds = Lexer.Lex("1e").Tokens.Select(t => t.Kind);
        Assert.Equal([SyntaxKind.NumericLiteralToken, SyntaxKind.IdentifierToken, SyntaxKind.EndOfFileToken], kinds);
    }

    [Fact]
    public void NestedBlockComment_IsSingleTrivia()
    {
        var token = Lexer.Lex("/* a /* b */ c */ SELECT").Tokens[0];
        Assert.Equal(SyntaxKind.KeywordToken, token.Kind);
        Assert.Equal("/* a /* b */ c */", token.LeadingTrivia[0].Text);
    }

    [Fact]
    public void TrailingTrivia_EndsAtFirstLineBreak()
    {
        var tokens = Lexer.Lex("SELECT 1 -- note\r\n\r\nFROM").Tokens;
        var one = tokens[1];
        var from = tokens[2];

        Assert.Equal(
            [SyntaxKind.WhitespaceTrivia, SyntaxKind.SingleLineCommentTrivia, SyntaxKind.EndOfLineTrivia],
            one.TrailingTrivia.Select(t => t.Kind));
        Assert.Equal("\r\n", one.TrailingTrivia[^1].Text);
        Assert.Equal([SyntaxKind.EndOfLineTrivia], from.LeadingTrivia.Select(t => t.Kind));
    }

    [Fact]
    public void UnterminatedString_DiagnosticSpansToEndOfInput()
    {
        var result = Lexer.Lex("SELECT 'abc");
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(TextSpan.FromBounds(7, 11), diagnostic.Span);
    }

    [Fact]
    public void UnexpectedCharacter_IsBadTokenWithDiagnostic()
    {
        var result = Lexer.Lex("SELECT ?");
        Assert.Equal(SyntaxKind.BadToken, result.Tokens[1].Kind);
        Assert.Equal(SyntaxDiagnosticIds.UnexpectedCharacter, Assert.Single(result.Diagnostics).Id);
    }

    private static void AssertLosslessAndContiguous(string text)
    {
        var tokens = Lexer.Lex(text).Tokens;

        Assert.Equal(SyntaxKind.EndOfFileToken, tokens[^1].Kind);

        var rebuilt = new StringBuilder(text.Length);
        var expectedStart = 0;
        foreach (var token in tokens)
        {
            Assert.Equal(expectedStart, token.FullSpan.Start);
            rebuilt.Append(token.ToFullString());
            expectedStart = token.FullSpan.End;
        }

        Assert.Equal(text, rebuilt.ToString());
    }
}
