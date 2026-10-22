using System.Text;
using CsCheck;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Tests;

public sealed class ParserTests
{
    /// <summary>Fragments that drive the parser through every construct, glued in random order.</summary>
    private static readonly string[] Fragments =
    [
        "SELECT ", "FROM ", "WHERE ", "GROUP BY ", "HAVING ", "ORDER BY ", "JOIN ", "LEFT JOIN ", "CROSS APPLY ",
        "ON ", "AS ", "INSERT INTO ", "VALUES ", "UPDATE ", "SET ", "DELETE ", "MERGE ", "USING ", "WHEN ", "THEN ",
        "WITH ", "cte ", "UNION ALL ", "CASE ", "END ", "ELSE ", "BEGIN ", "BEGIN TRY ", "END TRY ", "BEGIN CATCH ",
        "IF ", "WHILE ", "DECLARE ", "@v ", "CREATE PROCEDURE p ", "OUTPUT ", "TOP (1) ", "(", ")", ",", ";", ".",
        "*", "=", "dbo", "t", "c", "[x]", "'s'", "1", "\nGO\n", "\n", " ", "-- c\n", "/* c */", "OVER (", "PIVOT ",
    ];

    private static readonly Gen<string> SqlLikeScripts =
        Gen.Int[0, Fragments.Length - 1].Array[0, 60].Select(indexes => string.Concat(indexes.Select(i => Fragments[i])));

    public static TheoryData<string> AllCorpusFiles => Corpus.Files();

    [Fact]
    public void Tree_IsLosslessForFragmentScripts() =>
        SqlLikeScripts.Sample(AssertLossless, iter: 20_000);

    [Fact]
    public void Tree_IsLosslessForArbitraryUtf16() =>
        Gen.String.Sample(AssertLossless, iter: 10_000);

    [Theory]
    [MemberData(nameof(AllCorpusFiles))]
    public void Tree_IsLosslessForCorpus(string file) => AssertLossless(Corpus.Read(file));

    [Theory]
    [InlineData("(", "")]
    [InlineData("((SELECT ", ")")]
    [InlineData("IF 1=1 ", "")]
    [InlineData("BEGIN ", "END ")]
    [InlineData("WHILE 1=1 BEGIN ", "")]
    public void DeepNesting_DoesNotOverflowTheStack(string open, string close)
    {
        const int depth = 100_000;
        AssertLossless(string.Concat(Enumerable.Repeat(open, depth)) + "SELECT 1" + string.Concat(Enumerable.Repeat(close, depth)));
    }

    [Fact]
    public void Statements_AreSplitWithoutTerminators()
    {
        var root = SyntaxTree.Parse("SELECT a FROM t\nUPDATE t SET a = 1\nDELETE FROM t WHERE a = 2\nEXEC dbo.p").Root;
        var kinds = root.DescendantNodes().Where(n => n.Kind.ToString().EndsWith("Statement", StringComparison.Ordinal)).Select(n => n.Kind);
        Assert.Equal(
            [SyntaxKind.SelectStatement, SyntaxKind.UpdateStatement, SyntaxKind.DeleteStatement, SyntaxKind.SimpleStatement],
            kinds);
    }

    [Theory]
    [InlineData("GRANT SELECT, INSERT ON dbo.t TO u")]
    [InlineData("ALTER TABLE t ADD CONSTRAINT fk FOREIGN KEY (a) REFERENCES u (a) ON DELETE SET NULL ON UPDATE CASCADE")]
    [InlineData("DROP TABLE IF EXISTS dbo.t")]
    [InlineData("ALTER TABLE t ALTER COLUMN a INT")]
    [InlineData("ALTER TABLE t DROP CONSTRAINT df")]
    [InlineData("ALTER DATABASE CURRENT SET RECOVERY SIMPLE")]
    [InlineData("SELECT a FROM t ORDER BY a OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY")]
    [InlineData("SELECT STRING_AGG(a, ',') WITHIN GROUP (ORDER BY a) FROM t")]
    [InlineData("SELECT a FROM t INNER MERGE JOIN u ON t.a = u.a")]
    [InlineData("SELECT a FROM t JOIN u ON t.a = u.a OPTION (MERGE JOIN, HASH JOIN)")]
    [InlineData("SELECT a FROM OPENROWSET(\n    BULK 'f.csv', SINGLE_CLOB) AS x")]
    public void ContextualStarterKeywords_DoNotSplitTheStatement(string sql)
    {
        var batch = SyntaxTree.Parse(sql).Root.ChildNodes().Single(n => n.Kind == SyntaxKind.Batch);
        Assert.Single(batch.ChildNodes());
    }

    [Theory]
    [InlineData("SET NOCOUNT ON\nSELECT a FROM t", SyntaxKind.SelectStatement)]
    [InlineData("SET IDENTITY_INSERT t ON\nINSERT INTO t (a) VALUES (1)", SyntaxKind.InsertStatement)]
    [InlineData("SET XACT_ABORT ON\nUPDATE t SET a = 1", SyntaxKind.UpdateStatement)]
    [InlineData("SET @e = 1\nTHROW 51000, @e, 1;", SyntaxKind.SimpleStatement)]
    public void StatementAfterSetOn_IsSeparate(string sql, SyntaxKind second)
    {
        var batch = SyntaxTree.Parse(sql).Root.ChildNodes().Single(n => n.Kind == SyntaxKind.Batch);
        Assert.Equal([SyntaxKind.SetStatement, second], batch.ChildNodes().Select(n => n.Kind));
    }

    [Fact]
    public void Label_AfterUnterminatedStatement_IsItsOwnStatement()
    {
        var batch = SyntaxTree.Parse("SET @x = 1\nErrorHandler:\n    RETURN 1").Root.ChildNodes().Single(n => n.Kind == SyntaxKind.Batch);
        Assert.Equal([SyntaxKind.SetStatement, SyntaxKind.LabelStatement, SyntaxKind.SimpleStatement], batch.ChildNodes().Select(n => n.Kind));
    }

    [Fact]
    public void Throw_IsTheThenStatementOfIf()
    {
        var statement = SyntaxTree.Parse("IF OBJECT_ID(N't') IS NULL\n    THROW 51000, 'missing', 1;").Root
            .DescendantNodes().Single(n => n.Kind == SyntaxKind.IfStatement);
        Assert.Contains(statement.ChildNodes(), n => n.Kind == SyntaxKind.SimpleStatement && n.FirstToken.Text == "THROW");
    }

    [Fact]
    public void UnclosedCall_IsClosedByJoinKeyword()
    {
        var tree = SyntaxTree.Parse("SELECT a FROM t JOIN u ON RTRIM(LTRIM(t.a\nLEFT OUTER JOIN v ON v.a = t.a");
        var from = tree.Root.DescendantNodes().Single(n => n.Kind == SyntaxKind.FromClause);
        Assert.Equal(2, from.ChildNodes().Count(n => n.Kind == SyntaxKind.JoinClause));
    }

    [Fact]
    public void BatchSeparator_SplitsBatches()
    {
        var root = SyntaxTree.Parse("SELECT 1\nGO\nSELECT 2\ngo 5\n").Root;
        Assert.Equal(
            [SyntaxKind.Batch, SyntaxKind.BatchSeparator, SyntaxKind.Batch, SyntaxKind.BatchSeparator],
            root.ChildNodes().Select(n => n.Kind));
    }

    [Fact]
    public void ModuleBody_RunsToEndOfBatch()
    {
        var root = SyntaxTree.Parse(Corpus.Read("lexical/procedure.sql")).Root;
        var module = root.DescendantNodes().Single(n => n.Kind == SyntaxKind.CreateModuleStatement);
        Assert.Contains(module.DescendantNodes(), n => n.Kind == SyntaxKind.QuerySpecification);
        Assert.Equal(SyntaxKind.BatchSeparator, root.ChildNodes().Last().Kind);
    }

    [Fact]
    public void UnclosedParenthesis_StopsAtStatementOnNextLine()
    {
        var tree = SyntaxTree.Parse("SELECT a FROM t WHERE a IN (1, 2\nSELECT b FROM u");
        Assert.Equal(2, tree.Root.DescendantNodes().Count(n => n.Kind == SyntaxKind.SelectStatement));
        Assert.Contains(tree.Diagnostics, d => d.Id == SyntaxDiagnosticIds.ExpectedCloseParen);
    }

    private static void AssertLossless(string text)
    {
        var tree = SyntaxTree.Parse(text);
        var builder = new StringBuilder();
        tree.Root.WriteTo(builder);
        Assert.Equal(text, builder.ToString());
        Assert.Equal(SyntaxKind.EndOfFileToken, tree.Root.LastToken.Kind);
    }
}
