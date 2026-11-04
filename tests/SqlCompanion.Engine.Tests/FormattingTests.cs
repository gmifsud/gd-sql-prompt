using CsCheck;
using SqlCompanion.Engine.Formatting;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Tests;

/// <summary>
/// Formatter invariants (roadmap Phase 1 gate): idempotence, token and comment preservation, statement-structure
/// preservation, correct edits — plus golden layouts for the default profile.
/// </summary>
public sealed class FormattingTests
{
    private static readonly StyleProfile Lf = StyleProfile.Default with { LineEndings = LineEndingPolicy.Lf };

    public static TheoryData<string> GoldenCases
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var row in Corpus.Files("formatting"))
            {
                if (row.Data.EndsWith(".input.sql", StringComparison.Ordinal))
                {
                    data.Add(row.Data[..^".input.sql".Length]);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, string> CorpusTimesProfiles
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var row in Corpus.Files())
            {
                foreach (var profile in StyleProfile.BuiltIn.Keys.Order(StringComparer.Ordinal))
                {
                    data.Add(row.Data, profile);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Default_MatchesGolden(string stem)
    {
        var result = SqlFormatter.Format(Corpus.Read(stem + ".input.sql"), StyleProfile.Default);

        Assert.Null(result.SkippedReason);
        Assert.Equal(Corpus.Read(stem + ".expected.sql"), result.Text);
    }

    [Theory]
    [MemberData(nameof(CorpusTimesProfiles))]
    public void Corpus_IsFormattedSafelyAndIdempotently(string file, string profileName)
    {
        var profile = StyleProfile.Find(profileName)!;
        var once = SqlFormatter.Format(Corpus.Read(file), profile);
        var twice = SqlFormatter.Format(once.Text, profile);

        Assert.Null(once.SkippedReason);
        Assert.Equal(once.Text, twice.Text); // format ∘ format = format
    }

    [Fact]
    public void RandomTokenSoup_NeverThrows_AndIsIdempotentWhenFormatted()
    {
        string[] vocabulary =
        [
            "SELECT", "select", "FROM", "WHERE", "AND", "OR", "BETWEEN", "CASE", "WHEN", "THEN", "ELSE", "END", "BEGIN",
            "IF", "WHILE", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "JOIN", "LEFT", "ON", "GROUP", "BY",
            "ORDER", "UNION", "ALL", "WITH", "AS", "DECLARE", "GO", "a", "b", "dbo", ".", ",", ";", "(", ")", "=", "<>",
            "-", "+", "*", "@x", "1", "'s'", "N'x'", "[q]", "-- c\n", "/* c */", "\n", "\n\n", " ", "\t",
        ];
        var soup = Gen.Int[0, vocabulary.Length - 1].Array[0, 60]
            .Select(indexes => string.Join(" ", indexes.Select(i => vocabulary[i])));

        soup.Sample(text =>
        {
            var once = SqlFormatter.Format(text, Lf);
            if (once.SkippedReason is null)
            {
                Assert.Equal(once.Text, SqlFormatter.Format(once.Text, Lf).Text);
            }
        }, iter: 3_000);
    }

    [Theory]
    [InlineData("select a , b from t", "SELECT a, b\nFROM t")]
    [InlineData("select a from t where x=1 and y between 1 and 2", "SELECT a\nFROM t\nWHERE x = 1 AND y BETWEEN 1 AND 2")]
    [InlineData("select count( * ) from dbo . t", "SELECT count(*)\nFROM dbo.t")]
    [InlineData("select count (*) from t", "SELECT count (*)\nFROM t")] // a space after an identifier is kept: INSERT INTO t (a)
    [InlineData("select a from t where b in(1,2)", "SELECT a\nFROM t\nWHERE b IN (1, 2)")]
    [InlineData("select convert (int, a) from t", "SELECT CONVERT(int, a)\nFROM t")]
    [InlineData("set @x=-1", "SET @x = -1")]
    [InlineData("lbl :\nselect 1", "lbl:\nSELECT 1")]
    public void Spacing_IsNormalised(string input, string expected) =>
        Assert.Equal(expected, SqlFormatter.Format(input + "\n", Lf).Text.TrimEnd('\n'));

    [Fact]
    public void Profiles_ChangeCasingCommasAndCollapse()
    {
        const string sql = "select alpha_column_name, beta_column_name, gamma_column_name from t where x = 1\n";
        var narrow = 40;

        Assert.Equal(
            "select\n    alpha_column_name,\n    beta_column_name,\n    gamma_column_name\nfrom t\nwhere x = 1\n",
            SqlFormatter.Format(sql, StyleProfile.Lowercase with { MaxLineWidth = narrow }).Text);
        Assert.Equal(
            "SELECT\n    alpha_column_name\n    , beta_column_name\n    , gamma_column_name\nFROM t\nWHERE x = 1\n",
            SqlFormatter.Format(sql, StyleProfile.LeadingCommas with { MaxLineWidth = narrow }).Text);
        Assert.Equal(
            "SELECT a FROM t WHERE x = 1\n",
            SqlFormatter.Format("select a\nfrom t\nwhere x = 1\n", StyleProfile.Compact).Text);
        Assert.Equal(
            "IF @a = 1\nBEGIN\n\tSELECT 1\nEND\n",
            SqlFormatter.Format("if @a=1 begin select 1 end\n", StyleProfile.Default with { UseTabs = true }).Text);
    }

    [Fact]
    public void LineEndings_FollowTheDocument_AndTheFinalNewlineIsKept()
    {
        Assert.Equal("SELECT a\r\nFROM t\r\n", SqlFormatter.Format("select a from t\r\n", StyleProfile.Default).Text);
        Assert.Equal("SELECT a\nFROM t\n", SqlFormatter.Format("select a\nfrom t\n", StyleProfile.Default).Text);
        Assert.Equal("SELECT a\nFROM t", SqlFormatter.Format("select a from t", Lf).Text);
    }

    [Fact]
    public void LiteralsAndBlockComments_KeepTheirOwnLineBreaks()
    {
        const string sql = "select 'line one\r\nline two' /* note\r\n   more */ from t\n";

        var result = SqlFormatter.Format(sql, Lf);

        Assert.Contains("'line one\r\nline two'", result.Text, StringComparison.Ordinal);
        Assert.Contains("/* note\r\n   more */", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT a, b", "SELECT ab", "token count")]
    [InlineData("SELECT a -- note\nFROM t", "SELECT a\nFROM t", "comments")]
    [InlineData("SELECT a,\n b\nFROM t", "SELECT a,\ngo\nFROM t", "token")]
    [InlineData("SELECT a, go FROM t", "SELECT a,\ngo\nFROM t", "structure")]
    public void Verify_RejectsLayoutsThatChangeTheProgram(string original, string formatted, string expectedReason)
    {
        var problem = SqlFormatter.Verify(SyntaxTree.Parse(original), SyntaxTree.Parse(formatted));

        Assert.NotNull(problem);
        Assert.Contains(expectedReason, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Edits_ApplyToTheFormattedText()
    {
        foreach (var row in Corpus.Files())
        {
            var text = Corpus.Read(row.Data);
            var expected = SqlFormatter.Format(text, StyleProfile.Default).Text;

            Assert.Equal(expected, Apply(text, SqlFormatter.ComputeEdits(text, StyleProfile.Default)));
        }
    }

    [Fact]
    public void RangeEdits_TouchOnlyTheSelectedLines()
    {
        const string text = "select a from t\nselect   b   from u\nselect c from v\n";
        var secondLine = text.IndexOf("select   b", StringComparison.Ordinal);

        var edits = SqlFormatter.ComputeEdits(text, Lf, new TextSpan(secondLine + 2, 3));
        var result = Apply(text, edits);

        Assert.All(edits, e => Assert.True(e.Span.Start >= secondLine - 1 && e.Span.End <= text.IndexOf("select c", StringComparison.Ordinal)));
        Assert.StartsWith("select a from t\nSELECT b\nFROM u\nselect c from v", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_ReturnsMinimalEdits()
    {
        var service = new SqlFormattingService(Lf);

        var edits = await service.FormatAsync(new DocumentSnapshot("file:///a.sql", 1, "select 1\n"), range: null, options: null, TestContext.Current.CancellationToken);

        var edit = Assert.Single(edits);
        Assert.Equal(new TextEdit(new TextSpan(0, 6), "SELECT"), edit);
    }

    [Fact]
    public void Printer_BreaksOnlyGroupsThatDoNotFit()
    {
        var list = Doc.Group(Doc.Concat(Doc.Text("f("), Doc.Nest(Doc.Concat(Doc.SoftLine, Doc.Text("a,"), Doc.Line, Doc.Text("b"))), Doc.SoftLine, Doc.Text(")")));

        Assert.Equal("f(a, b)", DocPrinter.Print(list, 80, "  ", "\n"));
        Assert.Equal("f(\n  a,\n  b\n)", DocPrinter.Print(list, 5, "  ", "\n"));
        Assert.Equal("x\n\ny", DocPrinter.Print(Doc.Concat(Doc.Text("x"), Doc.BlankLine, Doc.BlankLine, Doc.HardLine, Doc.Text("y")), 80, "  ", "\n"));
    }

    private static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var builder = new System.Text.StringBuilder(text);
        foreach (var edit in edits.OrderByDescending(e => e.Span.Start))
        {
            builder.Remove(edit.Span.Start, edit.Span.Length).Insert(edit.Span.Start, edit.NewText);
        }

        return builder.ToString();
    }
}
