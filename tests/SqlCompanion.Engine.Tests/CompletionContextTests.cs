using SqlCompanion.Engine.Binding;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Tests;

public sealed class CompletionContextTests
{
    /// <summary>Spec §14 scenario A: <c>c.</c> resolves to dbo.Customers.</summary>
    [Fact]
    public void ScenarioA_AliasResolvesInSelectList()
    {
        var context = Resolve("SELECT c.| \nFROM dbo.Customers AS c;");
        Assert.Equal(CompletionClause.SelectList, context.Clause);
        var source = Assert.Single(context.Sources);
        Assert.Equal(("c", "name:dbo.customers"), (source.ExposedName, source.Descriptor));
    }

    /// <summary>Spec §14 scenario B: both joined tables are visible after JOIN … (before ON is typed).</summary>
    [Fact]
    public void ScenarioB_JoinedTablesAreInScope()
    {
        var context = Resolve("SELECT\nFROM dbo.Orders AS o\nJOIN dbo.Customers AS c ON |");
        Assert.Equal(CompletionClause.JoinCondition, context.Clause);
        Assert.Equal(["o", "c"], context.Sources.Select(s => s.ExposedName));
    }

    [Theory]
    [InlineData("SELECT a, | FROM t", CompletionClause.SelectList)]
    [InlineData("SELECT a FROM |", CompletionClause.TableReference)]
    [InlineData("SELECT a FROM t LEFT JOIN |", CompletionClause.TableReference)]
    [InlineData("SELECT a FROM t WHERE |", CompletionClause.Where)]
    [InlineData("SELECT a FROM t WHERE a = 1 | ORDER BY a", CompletionClause.Where)]
    [InlineData("SELECT a FROM t GROUP BY |", CompletionClause.GroupBy)]
    [InlineData("SELECT a FROM t GROUP BY a HAVING |", CompletionClause.Having)]
    [InlineData("SELECT a FROM t ORDER BY |", CompletionClause.OrderBy)]
    [InlineData("UPDATE t SET |", CompletionClause.SetList)]
    [InlineData("UPDATE |", CompletionClause.TableReference)]
    [InlineData("DELETE |\nFROM [dbo].[T] as t WHERE t.Id = 1", CompletionClause.TableReference)]
    [InlineData("INSERT | INTO t (a) VALUES (1)", CompletionClause.TableReference)]
    [InlineData("INSERT INTO t (|", CompletionClause.InsertColumns)]
    [InlineData("INSERT INTO t (a) VALUES (|", CompletionClause.Values)]
    [InlineData("DELETE FROM t WHERE |", CompletionClause.Where)]
    [InlineData("SELECT 'unterminated | FROM t", CompletionClause.SelectList)]
    [InlineData("SELECT a,\n\n    |\n\nSELECT 1", CompletionClause.SelectList)]
    [InlineData("INSERT INTO t (a) VALUES (\n    1\n    ,2\n    |\n\nIF 1 = 1 PRINT 1", CompletionClause.Values)]
    public void Clause_IsResolvedWhileTyping(string markedText, CompletionClause expected) =>
        Assert.Equal(expected, Resolve(markedText).Clause);

    [Fact]
    public void EmptySchemaPart_IsKeptPositionally()
    {
        var source = Assert.Single(Resolve("SELECT b.| FROM msdb..backupset b").Sources);
        Assert.Equal(("b", "name:msdb..backupset"), (source.ExposedName, source.Descriptor));
    }

    [Fact]
    public void PartialWordThatIsAKeyword_IsTreatedAsTheWordBeingTyped()
    {
        var context = Resolve("SELECT @UserId = Use|\n    FROM dbo.aspnet_Users WHERE LoweredUserName = @u");
        Assert.Equal(CompletionClause.SelectList, context.Clause);
        Assert.Equal(["aspnet_users"], context.Sources.Select(s => s.ExposedName));
    }

    [Theory]
    [InlineData("SELECT|* FROM t", CompletionClause.SelectList)]
    [InlineData("SELECT a FROM t WHERE a IN|(1, 2)", CompletionClause.Where)]
    public void CompleteKeywordAtCaret_IsNotTreatedAsPartialWord(string markedText, CompletionClause expected) =>
        Assert.Equal(expected, Resolve(markedText).Clause);

    [Fact]
    public void TrailingCommaBeforeNextSelect_DoesNotMergeQueries()
    {
        var context = Resolve("SELECT a,\n    |\n\nSELECT COUNT(*) FROM #PageIndex");
        Assert.Equal(CompletionClause.SelectList, context.Clause);
        Assert.Empty(context.Sources);
    }

    [Fact]
    public void Subquery_IsItsOwnScope()
    {
        var context = Resolve("SELECT a FROM t WHERE a IN (SELECT b FROM u WHERE |)");
        Assert.Equal(CompletionClause.Where, context.Clause);
        Assert.Equal(["u"], context.Sources.Select(s => s.ExposedName));
    }

    [Fact]
    public void DerivedTable_IsExposedByAlias()
    {
        var context = Resolve("SELECT | FROM (SELECT a FROM t) AS d CROSS APPLY dbo.f(d.a) AS f");
        Assert.Equal([("d", "derived"), ("f", "derived")], context.Sources.Select(s => (s.ExposedName, s.Descriptor)));
    }

    [Fact]
    public void UpdateTargetNamingFromAlias_IsNotDuplicated()
    {
        var context = Resolve("UPDATE c SET c.Name = | FROM dbo.Customers AS c JOIN dbo.Orders o ON o.CustomerId = c.Id");
        Assert.Equal(CompletionClause.SetList, context.Clause);
        Assert.Equal(["c", "o"], context.Sources.Select(s => s.ExposedName));
    }

    [Fact]
    public void CteBody_IsResolvedInsideWithClause()
    {
        var context = Resolve("WITH x AS (SELECT | FROM dbo.T) SELECT * FROM x");
        Assert.Equal(CompletionClause.SelectList, context.Clause);
        Assert.Equal(["t"], context.Sources.Select(s => s.ExposedName));
    }

    private static CompletionContext Resolve(string markedText)
    {
        var position = markedText.IndexOf('|', StringComparison.Ordinal);
        return CompletionContextResolver.Resolve(SyntaxTree.Parse(markedText.Remove(position, 1)), position);
    }
}
