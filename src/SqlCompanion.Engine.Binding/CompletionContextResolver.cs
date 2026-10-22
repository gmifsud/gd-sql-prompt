using System.Collections.Immutable;
using System.Text;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Binding;

/// <summary>Syntactic position classes that drive completion behaviour.</summary>
public enum CompletionClause
{
    None,
    SelectList,
    TableReference,
    JoinCondition,
    Where,
    GroupBy,
    Having,
    OrderBy,
    SetList,
    InsertColumns,
    Values,
    Other,
}

/// <summary>A table source visible in the scope at the caret.</summary>
/// <param name="ExposedName">The name columns are qualified with: the alias, else the base object name (normalised, lower case).</param>
/// <param name="Descriptor"><c>name:schema.object</c>, <c>var:@t</c> or <c>derived</c>.</param>
/// <param name="Span">Span of the table source in the document.</param>
public sealed record SourceBinding(string ExposedName, string Descriptor, TextSpan Span);

/// <param name="Clause">Clause containing the caret.</param>
/// <param name="Sources">Sources of the innermost scope (query block or DML statement). Empty when there is no scope.</param>
/// <param name="Scope">Innermost scope node, if any.</param>
public sealed record CompletionContext(CompletionClause Clause, ImmutableArray<SourceBinding> Sources, SyntaxNode? Scope)
{
    public static CompletionContext None { get; } = new(CompletionClause.None, [], null);
}

/// <summary>
/// Resolves the completion context at a caret from the tolerant CST (Phase 0 spike S2; seed of the
/// Phase 2 binder). Scope is the innermost query block or DML statement containing the caret; outer
/// (correlated) scopes are not yet included.
/// </summary>
/// <remarks>
/// Clause regions start after a clause's leading keyword(s) and run to the start of the next clause's
/// keyword, so a caret placed in trailing whitespace belongs to the clause being typed.
/// </remarks>
public static class CompletionContextResolver
{
    private static bool IsScope(SyntaxKind kind) => kind is
        SyntaxKind.QuerySpecification or
        SyntaxKind.UpdateStatement or
        SyntaxKind.DeleteStatement or
        SyntaxKind.InsertStatement or
        SyntaxKind.MergeStatement;

    /// <summary>Nodes that introduce a nested scope and are therefore opaque to the enclosing scope's analysis.</summary>
    private static bool IsNestedScopeBoundary(SyntaxKind kind) => IsScope(kind) || kind is SyntaxKind.Subquery or SyntaxKind.WithClause;

    private static bool IsTableSource(SyntaxKind kind) => kind is
        SyntaxKind.NamedTableSource or
        SyntaxKind.DerivedTableSource or
        SyntaxKind.FunctionTableSource or
        SyntaxKind.VariableTableSource;

    /// <summary>
    /// Keywords after which a statement cannot be complete. When a scope ends with one of these (or with a
    /// comma, dot, operator or open parenthesis) a caret on the following blank lines still belongs to it.
    /// </summary>
    private static readonly HashSet<string> ContinuationKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "ON", "JOIN", "BY", "SET", "INTO", "AS", "IN", "LIKE",
        "BETWEEN", "IS", "CASE", "WHEN", "THEN", "ELSE", "HAVING", "UNION", "EXCEPT", "INTERSECT", "ALL",
        "DISTINCT", "TOP", "INNER", "LEFT", "RIGHT", "FULL", "OUTER", "CROSS", "EXISTS", "ESCAPE", "VALUES",
        "UPDATE", "DELETE", "INSERT", "MERGE", "OVER", "APPLY", "USING",
    };

    public static CompletionContext Resolve(SyntaxTree tree, int position)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // A half-typed word can collide with a keyword: `SELECT @Id = Use▌` is the start of UserId, not a USE
        // statement. When a keyword ending at the caret sits where an operand is expected and has split the
        // statement, re-parse with it as a same-length identifier (offsets unchanged).
        if (KeywordSplittingStatementAt(tree.Root, position) is { } keyword)
        {
            var text = string.Concat(tree.Text.AsSpan(0, keyword.Position), new string('_', keyword.Text.Length), tree.Text.AsSpan(keyword.Span.End));
            tree = SyntaxTree.Parse(text);
        }

        var scope = FindInnermostScope(tree.Root, position, NextTokenStarts(tree.Root));
        if (scope is null)
        {
            return CompletionContext.None;
        }

        var clause = ResolveClause(scope, position);
        var sources = clause switch
        {
            CompletionClause.InsertColumns => TargetSources(scope),
            _ when scope.Kind == SyntaxKind.InsertStatement => [],
            _ => ScopeSources(scope),
        };

        return new CompletionContext(clause, sources, scope);
    }

    private static SyntaxToken? KeywordSplittingStatementAt(SyntaxNode root, int position)
    {
        SyntaxToken? previous = null;
        foreach (var token in root.DescendantTokens())
        {
            if (token.Kind == SyntaxKind.KeywordToken && token.Span.End == position && token.Span.Length > 0)
            {
                var operandExpected = previous is not null && IsContinuation(previous);
                var startsStatement = root.DescendantNodes().Any(n =>
                    ReferenceEquals(n.FirstToken, token) && n.Kind.ToString().EndsWith("Statement", StringComparison.Ordinal));
                return operandExpected && startsStatement ? token : null;
            }

            if (token.Span.Start > position)
            {
                return null;
            }

            previous = token;
        }

        return null;
    }

    /// <summary>Maps each token's position to the position of the token that follows it.</summary>
    private static Dictionary<int, int> NextTokenStarts(SyntaxNode root)
    {
        var map = new Dictionary<int, int>();
        SyntaxToken? previous = null;
        foreach (var token in root.DescendantTokens())
        {
            if (previous is not null)
            {
                map[previous.Position] = token.Position;
            }

            previous = token;
        }

        return map;
    }

    private static bool IsContinuation(SyntaxToken token) => token.Kind switch
    {
        SyntaxKind.CommaToken or SyntaxKind.DotToken or SyntaxKind.OpenParenToken
            or SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.AsteriskToken or SyntaxKind.SlashToken
            or SyntaxKind.PercentToken or SyntaxKind.AmpersandToken or SyntaxKind.BarToken or SyntaxKind.BarBarToken
            or SyntaxKind.CaretToken or SyntaxKind.EqualsToken or SyntaxKind.NotEqualsToken or SyntaxKind.LessThanToken
            or SyntaxKind.LessThanEqualsToken or SyntaxKind.GreaterThanToken or SyntaxKind.GreaterThanEqualsToken => true,
        SyntaxKind.KeywordToken => ContinuationKeywords.Contains(token.Text),
        _ => false,
    };

    private static bool ContainsCaret(SyntaxNode node, int position, Dictionary<int, int> nextTokenStarts)
    {
        if (node.ContainsCaret(position))
        {
            return true;
        }

        // An incomplete construct extends across blank lines up to the next token.
        return position > node.Span.End
            && IsIncomplete(node)
            && nextTokenStarts.TryGetValue(node.LastToken.Position, out var next)
            && position <= next;
    }

    /// <summary>
    /// A construct is incomplete when it ends with a continuation token or its rightmost group is still open
    /// (leading-comma lists inside an unclosed <c>(</c>).
    /// </summary>
    private static bool IsIncomplete(SyntaxNode node)
    {
        if (IsContinuation(node.LastToken))
        {
            return true;
        }

        for (var current = node; current.Children[^1] is SyntaxNode last; current = last)
        {
            if (last.Kind is SyntaxKind.ParenthesizedExpression or SyntaxKind.Subquery
                && last.LastToken.Kind != SyntaxKind.CloseParenToken)
            {
                return true;
            }
        }

        return false;
    }

    private static SyntaxNode? FindInnermostScope(SyntaxNode node, int position, Dictionary<int, int> nextTokenStarts)
    {
        SyntaxNode? best = null;
        var bestDepth = -1;
        Visit(node, 0);
        return best;

        void Visit(SyntaxNode current, int depth)
        {
            if (!ContainsCaret(current, position, nextTokenStarts))
            {
                return;
            }

            if (IsScope(current.Kind) && depth > bestDepth)
            {
                best = current;
                bestDepth = depth;
            }

            foreach (var child in current.ChildNodes())
            {
                Visit(child, depth + 1);
            }
        }
    }

    private static CompletionClause ResolveClause(SyntaxNode scope, int position)
    {
        var clause = CompletionClause.None;
        foreach (var (start, kind) in RegionMarkers(scope))
        {
            if (start > position)
            {
                break;
            }

            clause = kind;
        }

        return clause;
    }

    /// <summary>Region starts in document order. The clause at a caret is the last marker at or before it.</summary>
    private static IEnumerable<(int Start, CompletionClause Clause)> RegionMarkers(SyntaxNode scope)
    {
        var markers = new List<(int, CompletionClause)>();
        var isDml = scope.Kind != SyntaxKind.QuerySpecification;
        if (isDml)
        {
            // The target region opens after UPDATE / DELETE [FROM] / INSERT [INTO] / MERGE [INTO], even before a target is typed.
            // Every head keyword opens it, so a caret between DELETE and FROM is a table position too.
            var headKeywords = scope.Children
                .TakeWhile(c => c is SyntaxToken || c.Kind is SyntaxKind.WithClause or SyntaxKind.TopClause)
                .OfType<SyntaxToken>()
                .Where(t => t.Kind == SyntaxKind.KeywordToken);
            foreach (var keyword in headKeywords)
            {
                markers.Add((keyword.Span.End, CompletionClause.TableReference));
            }
        }

        foreach (var node in scope.DescendantNodes(n => !IsNestedScopeBoundary(n.Kind)))
        {
            if (IsNestedScopeBoundary(node.Kind))
            {
                continue;
            }

            switch (node.Kind)
            {
                case SyntaxKind.SelectClause:
                    markers.Add((node.FirstToken.Span.End, CompletionClause.SelectList));
                    break;
                case SyntaxKind.TopClause:
                    markers.Add((node.Span.Start, CompletionClause.Other));
                    markers.Add((node.Span.End, isDml ? CompletionClause.TableReference : CompletionClause.SelectList));

                    break;
                case SyntaxKind.FromClause:
                    markers.Add((node.FirstToken.Span.End, CompletionClause.TableReference));
                    break;
                case SyntaxKind.JoinClause:
                    markers.Add((JoinKeywordEnd(node), CompletionClause.TableReference));
                    break;
                case SyntaxKind.OnClause:
                    markers.Add((node.FirstToken.Span.End, CompletionClause.JoinCondition));
                    break;
                case SyntaxKind.MergeUsingClause:
                    markers.Add((node.FirstToken.Span.End, CompletionClause.TableReference));
                    break;
                case SyntaxKind.WhereClause:
                    markers.Add((HeadEnd(node), CompletionClause.Where));
                    break;
                case SyntaxKind.GroupByClause:
                    markers.Add((HeadEnd(node), CompletionClause.GroupBy));
                    break;
                case SyntaxKind.HavingClause:
                    markers.Add((HeadEnd(node), CompletionClause.Having));
                    break;
                case SyntaxKind.OrderByClause:
                    markers.Add((HeadEnd(node), CompletionClause.OrderBy));
                    break;
                case SyntaxKind.IntoClause or SyntaxKind.OffsetClause or SyntaxKind.ForClause
                    or SyntaxKind.OptionClause or SyntaxKind.OutputClause or SyntaxKind.MergeWhenClause:
                    markers.Add((node.Span.Start, CompletionClause.Other));
                    break;
                case SyntaxKind.DmlTarget:
                    markers.Add((node.Span.Start, CompletionClause.TableReference));
                    break;
                case SyntaxKind.SetClause:
                    markers.Add((node.FirstToken.Span.End, CompletionClause.SetList));
                    break;
                case SyntaxKind.InsertColumnList:
                    markers.Add((node.FirstToken.Span.End, CompletionClause.InsertColumns));
                    if (node.LastToken.Kind == SyntaxKind.CloseParenToken)
                    {
                        markers.Add((node.LastToken.Span.End, CompletionClause.Other));
                    }

                    break;
                case SyntaxKind.ValuesClause:
                    markers.Add((node.FirstToken.Span.End, CompletionClause.Values));
                    break;
            }
        }

        return markers.OrderBy(m => m.Item1);
    }

    /// <summary>End of <c>BY</c> in GROUP BY / ORDER BY, else end of the leading keyword.</summary>
    private static int HeadEnd(SyntaxNode clause)
    {
        var tokens = clause.ChildTokens().Take(2).ToArray();
        return tokens.Length == 2 && tokens[1].Text.Equals("BY", StringComparison.OrdinalIgnoreCase)
            ? tokens[1].Span.End
            : tokens[0].Span.End;
    }

    private static int JoinKeywordEnd(SyntaxNode join)
    {
        var keywords = join.ChildTokens().ToArray();
        var joinWord = keywords.FirstOrDefault(t =>
            t.Text.Equals("JOIN", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("APPLY", StringComparison.OrdinalIgnoreCase));
        return (joinWord ?? keywords.LastOrDefault() ?? join.FirstToken).Span.End;
    }

    private static ImmutableArray<SourceBinding> ScopeSources(SyntaxNode scope)
    {
        var targets = TargetSources(scope);
        var fromSources = scope
            .DescendantNodes(n => !IsNestedScopeBoundary(n.Kind) && !IsTableSource(n.Kind) && n.Kind != SyntaxKind.DmlTarget)
            .Where(n => IsTableSource(n.Kind) && !IsInsideDmlTarget(scope, n))
            .Select(Bind)
            .OfType<SourceBinding>()
            .ToList();

        // A DML target that names a FROM alias (UPDATE c … FROM dbo.Customers c) is the same source.
        var exposed = fromSources.Select(s => s.ExposedName).ToHashSet(StringComparer.Ordinal);
        return [.. targets.Where(t => !exposed.Contains(t.ExposedName)), .. fromSources];
    }

    private static bool IsInsideDmlTarget(SyntaxNode scope, SyntaxNode source) =>
        scope.ChildNodes().Any(c => c.Kind == SyntaxKind.DmlTarget && c.Span.Start <= source.Span.Start && source.Span.End <= c.Span.End);

    private static ImmutableArray<SourceBinding> TargetSources(SyntaxNode scope) =>
    [
        .. scope.ChildNodes()
            .Where(c => c.Kind == SyntaxKind.DmlTarget)
            .SelectMany(c => c.ChildNodes())
            .Where(n => IsTableSource(n.Kind))
            .Select(Bind)
            .OfType<SourceBinding>(),
    ];

    private static SourceBinding? Bind(SyntaxNode source)
    {
        var alias = source.ChildNodes().LastOrDefault(n => n.Kind == SyntaxKind.Alias);
        var aliasName = alias?.ChildTokens().LastOrDefault(t =>
            t.Kind is SyntaxKind.IdentifierToken or SyntaxKind.QuotedIdentifierToken or SyntaxKind.StringLiteralToken);
        var isPivot = source.ChildTokens().Any(t =>
            t.Text.Equals("PIVOT", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("UNPIVOT", StringComparison.OrdinalIgnoreCase));

        switch (source.Kind)
        {
            case SyntaxKind.NamedTableSource when !isPivot:
            {
                var parts = NameParts(source.ChildNodes().First(n => n.Kind == SyntaxKind.MultipartName));
                if (parts.Count == 0)
                {
                    return null;
                }

                var exposed = aliasName is not null ? Normalize(aliasName.Text) : parts[^1];
                return new SourceBinding(exposed, "name:" + string.Join('.', parts), source.Span);
            }

            case SyntaxKind.VariableTableSource when !isPivot && !source.ChildTokens().Any(t => t.Kind == SyntaxKind.DotToken):
            {
                var variable = source.FirstToken.Text.ToLowerInvariant();
                return new SourceBinding(aliasName is not null ? Normalize(aliasName.Text) : variable, "var:" + variable, source.Span);
            }

            default:
                return aliasName is null ? null : new SourceBinding(Normalize(aliasName.Text), "derived", source.Span);
        }
    }

    /// <summary>
    /// Positional name parts. Empty parts are kept: <c>msdb..backupset</c> is database <c>msdb</c>, default
    /// schema, table <c>backupset</c> — not schema <c>msdb</c>. A trailing dot while typing adds no part.
    /// </summary>
    private static List<string> NameParts(SyntaxNode multipartName)
    {
        var parts = new List<string> { string.Empty };
        foreach (var token in multipartName.ChildTokens())
        {
            if (token.Kind == SyntaxKind.DotToken)
            {
                parts.Add(string.Empty);
            }
            else
            {
                parts[^1] = Normalize(token.Text);
            }
        }

        if (parts.Count > 1 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts.Any(p => p.Length > 0) ? parts : [];
    }

    /// <summary>Removes identifier/string delimiters, unescapes doubled delimiters and lower-cases.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length >= 2)
        {
            var (open, close) = (text[0], text[^1]);
            if ((open, close) is ('[', ']') or ('"', '"') or ('\'', '\''))
            {
                var inner = text[1..^1];
                var escaped = close.ToString();
                text = inner.Replace(escaped + escaped, escaped, StringComparison.Ordinal);
            }
            else if (open == '[' || open == '"' || open == '\'')
            {
                text = text[1..]; // unterminated while typing
            }
        }

        return text.ToLowerInvariant();
    }
}
