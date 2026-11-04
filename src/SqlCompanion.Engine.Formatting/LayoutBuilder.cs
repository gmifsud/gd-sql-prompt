using System.Collections.Frozen;
using System.Collections.Immutable;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Formatting;

/// <summary>
/// Builds the layout <see cref="Doc"/> for a CST. Every token is emitted exactly once, in source order, with its
/// comments; only whitespace between tokens and keyword casing change. Statements the parser did not fully
/// understand (opaque, simple/DDL, or carrying a syntax diagnostic) are emitted verbatim.
/// </summary>
internal sealed class LayoutBuilder
{
    private static readonly FrozenSet<SyntaxKind> StatementKinds = FrozenSet.Create(
        SyntaxKind.EmptyStatement, SyntaxKind.LabelStatement, SyntaxKind.SelectStatement, SyntaxKind.InsertStatement,
        SyntaxKind.UpdateStatement, SyntaxKind.DeleteStatement, SyntaxKind.MergeStatement, SyntaxKind.DeclareStatement,
        SyntaxKind.SetStatement, SyntaxKind.IfStatement, SyntaxKind.WhileStatement, SyntaxKind.BlockStatement,
        SyntaxKind.TryCatchStatement, SyntaxKind.CreateModuleStatement, SyntaxKind.SimpleStatement,
        SyntaxKind.OpaqueStatement, SyntaxKind.ErrorNode, SyntaxKind.BatchSeparator);

    /// <summary>Statements whose own tokens are laid out; their nested statements are judged separately.</summary>
    private static readonly FrozenSet<SyntaxKind> ControlFlowKinds = FrozenSet.Create(
        SyntaxKind.IfStatement, SyntaxKind.ElseClause, SyntaxKind.WhileStatement, SyntaxKind.BlockStatement,
        SyntaxKind.TryCatchStatement, SyntaxKind.CreateModuleStatement);

    private static readonly FrozenSet<SyntaxKind> LeafFormattedKinds = FrozenSet.Create(
        SyntaxKind.SelectStatement, SyntaxKind.InsertStatement, SyntaxKind.UpdateStatement, SyntaxKind.DeleteStatement,
        SyntaxKind.MergeStatement, SyntaxKind.DeclareStatement, SyntaxKind.SetStatement, SyntaxKind.LabelStatement,
        SyntaxKind.EmptyStatement);

    /// <summary>Clauses that start a new line inside a statement or query.</summary>
    private static readonly FrozenSet<SyntaxKind> ClauseKinds = FrozenSet.Create(
        SyntaxKind.WithClause, SyntaxKind.QueryExpression, SyntaxKind.QuerySpecification, SyntaxKind.SelectClause,
        SyntaxKind.IntoClause, SyntaxKind.FromClause, SyntaxKind.WhereClause, SyntaxKind.GroupByClause,
        SyntaxKind.HavingClause, SyntaxKind.OrderByClause, SyntaxKind.OffsetClause, SyntaxKind.ForClause,
        SyntaxKind.OptionClause, SyntaxKind.SetClause, SyntaxKind.OutputClause, SyntaxKind.ValuesClause,
        SyntaxKind.MergeUsingClause, SyntaxKind.MergeWhenClause);

    /// <summary>Reserved keywords that are called like functions: no space before their <c>(</c>.</summary>
    private static readonly FrozenSet<string> FunctionKeywords = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "CONVERT", "TRY_CONVERT", "COALESCE", "NULLIF", "LEFT", "RIGHT", "IDENTITY", "UPDATE", "CONTAINS",
        "CONTAINSTABLE", "FREETEXT", "FREETEXTTABLE", "OPENDATASOURCE", "OPENQUERY", "OPENROWSET", "OPENXML",
        "SEMANTICKEYPHRASETABLE", "SEMANTICSIMILARITYDETAILSTABLE", "SEMANTICSIMILARITYTABLE");

    private readonly StyleProfile _profile;
    private readonly string _text;
    private readonly ImmutableArray<SyntaxToken> _tokens;
    private readonly Dictionary<SyntaxToken, int> _index;
    private readonly bool[] _hasDiagnostic;
    private SyntaxToken? _blankAllowedFor;

    private LayoutBuilder(SyntaxTree tree, StyleProfile profile)
    {
        _profile = profile;
        _text = tree.Text;
        _tokens = [.. tree.Root.DescendantTokens()];
        _index = new Dictionary<SyntaxToken, int>(_tokens.Length, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _tokens.Length; i++)
        {
            _index[_tokens[i]] = i;
        }

        // Tokens are in position order: binary-search the first token a diagnostic can touch, O(D log N + touched).
        _hasDiagnostic = new bool[_tokens.Length];
        foreach (var diagnostic in tree.Diagnostics)
        {
            int low = 0, high = _tokens.Length - 1;
            while (low < high)
            {
                var mid = (low + high) / 2;
                if (_tokens[mid].FullSpan.End < diagnostic.Span.Start)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            for (var i = low; i < _tokens.Length && _tokens[i].FullSpan.Start <= diagnostic.Span.End; i++)
            {
                if (_tokens[i].FullSpan.End >= diagnostic.Span.Start)
                {
                    _hasDiagnostic[i] = true;
                }
            }
        }
    }

    private Doc ClauseBreak => _profile.CollapseShortQueries ? Doc.Line : Doc.HardLine;

    public static Doc Build(SyntaxTree tree, StyleProfile profile) => new LayoutBuilder(tree, profile).Unit(tree.Root);

    // ───────────────────────────── statements ─────────────────────────────

    private Doc Unit(SyntaxNode root)
    {
        var parts = new List<Doc>();
        var any = false;
        foreach (var child in root.Children)
        {
            if (child is SyntaxNode { Kind: SyntaxKind.Batch } batch)
            {
                foreach (var statement in batch.Children)
                {
                    AddStatement(parts, statement, ref any);
                }
            }
            else if (child is SyntaxToken { Kind: SyntaxKind.EndOfFileToken } eof)
            {
                _blankAllowedFor = eof;
                parts.Add(Emit(eof));
            }
            else
            {
                AddStatement(parts, child, ref any);
            }
        }

        return Doc.Concat(parts);
    }

    private void AddStatement(List<Doc> parts, SyntaxElement statement, ref bool any)
    {
        if (any)
        {
            parts.Add(Doc.HardLine);
        }

        parts.Add(Statement(statement));
        any = true;
    }

    private Doc Statement(SyntaxElement element)
    {
        if (element is SyntaxToken token)
        {
            return Emit(token);
        }

        var node = (SyntaxNode)element;
        _blankAllowedFor = node.FirstToken;
        if (!IsFormattable(node))
        {
            return VerbatimNode(node);
        }

        return node.Kind switch
        {
            SyntaxKind.IfStatement or SyntaxKind.WhileStatement or SyntaxKind.BlockStatement
                or SyntaxKind.TryCatchStatement or SyntaxKind.CreateModuleStatement => ControlFlow(node),
            SyntaxKind.SelectStatement or SyntaxKind.InsertStatement or SyntaxKind.UpdateStatement
                or SyntaxKind.DeleteStatement or SyntaxKind.MergeStatement => ClauseContainer(node),
            SyntaxKind.DeclareStatement or SyntaxKind.SetStatement => KeywordThenList(node),
            _ => InlineChildren(node.Children, 0, node.Children.Length),
        };
    }

    private bool IsFormattable(SyntaxNode node)
    {
        var controlFlow = ControlFlowKinds.Contains(node.Kind);
        if (!controlFlow && !LeafFormattedKinds.Contains(node.Kind))
        {
            return false;
        }

        return !HasProblem(node, controlFlow);
    }

    /// <summary>A syntax diagnostic or unparsed region in the node's own tokens (for control flow, outside nested statements).</summary>
    private bool HasProblem(SyntaxNode node, bool skipNestedStatements)
    {
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case SyntaxToken token when _hasDiagnostic[_index[token]]:
                    return true;
                case SyntaxNode { Kind: SyntaxKind.ErrorNode or SyntaxKind.OpaqueStatement }:
                    return true;
                case SyntaxNode nested when skipNestedStatements && StatementKinds.Contains(nested.Kind):
                    break;
                case SyntaxNode nested when nested.Kind == SyntaxKind.ModuleHeader:
                    break; // emitted verbatim
                case SyntaxNode nested when HasProblem(nested, skipNestedStatements && nested.Kind == SyntaxKind.ElseClause):
                    return true;
            }
        }

        return false;
    }

    /// <summary>IF / WHILE / BEGIN…END / TRY…CATCH / CREATE module: nested statements indented, BEGIN aligned with its owner.</summary>
    private Doc ControlFlow(SyntaxNode node)
    {
        var parts = new List<Doc>();
        var previousWasStatement = false;
        var first = true;
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case SyntaxNode statement when StatementKinds.Contains(statement.Kind):
                    if (statement.Kind == SyntaxKind.BlockStatement && node.Kind is SyntaxKind.IfStatement or SyntaxKind.WhileStatement or SyntaxKind.ElseClause
                        || node.Kind == SyntaxKind.CreateModuleStatement)
                    {
                        parts.Add(Doc.HardLine);
                        parts.Add(Statement(statement));
                    }
                    else if (node.Kind == SyntaxKind.ElseClause && statement.Kind == SyntaxKind.IfStatement)
                    {
                        parts.Add(Statement(statement)); // ELSE IF on one line
                    }
                    else
                    {
                        parts.Add(Doc.Nest(Doc.Concat(Doc.HardLine, Statement(statement))));
                    }

                    previousWasStatement = true;
                    break;
                case SyntaxNode { Kind: SyntaxKind.ElseClause } elseClause:
                    parts.Add(Doc.HardLine);
                    parts.Add(ControlFlow(elseClause));
                    previousWasStatement = true;
                    break;
                case SyntaxNode { Kind: SyntaxKind.ModuleHeader } header:
                    parts.Add(VerbatimNode(header));
                    previousWasStatement = false;
                    break;
                case SyntaxNode { Kind: SyntaxKind.Expression } condition:
                    parts.Add(Predicate(condition.Children, 0, condition.Children.Length));
                    previousWasStatement = false;
                    break;
                case SyntaxToken token:
                    if (!first && (previousWasStatement || IsWord(token, "BEGIN") || IsWord(token, "END")))
                    {
                        parts.Add(Doc.HardLine);
                    }

                    parts.Add(Emit(token));
                    previousWasStatement = false;
                    break;
                default:
                    parts.Add(InlineElement(child));
                    previousWasStatement = false;
                    break;
            }

            first = false;
        }

        return Doc.Concat(parts);
    }

    /// <summary>A DML statement or query specification: each clause starts a line (or a soft line with collapse).</summary>
    private Doc ClauseContainer(SyntaxNode node)
    {
        var parts = new List<Doc>();
        var started = false;
        foreach (var child in node.Children)
        {
            if (child is SyntaxNode clause && (ClauseKinds.Contains(clause.Kind)
                || clause.Kind == SyntaxKind.OnClause && node.Kind == SyntaxKind.MergeStatement))
            {
                if (started)
                {
                    parts.Add(ClauseBreak);
                }

                parts.Add(Clause(clause));
            }
            else
            {
                parts.Add(InlineElement(child));
            }

            started = true;
        }

        var doc = Doc.Concat(parts);
        return _profile.CollapseShortQueries ? Doc.Group(doc) : doc;
    }

    private Doc Clause(SyntaxNode clause) => clause.Kind switch
    {
        SyntaxKind.QueryExpression => Query(clause),
        SyntaxKind.QuerySpecification => ClauseContainer(clause),
        SyntaxKind.SelectClause or SyntaxKind.GroupByClause or SyntaxKind.OrderByClause or SyntaxKind.SetClause => KeywordThenList(clause),
        SyntaxKind.WhereClause or SyntaxKind.HavingClause or SyntaxKind.OnClause => KeywordThenPredicate(clause),
        SyntaxKind.FromClause => From(clause),
        SyntaxKind.ValuesClause => ListAfterFirst(clause),
        SyntaxKind.WithClause => With(clause),
        _ => InlineChildren(clause.Children, 0, clause.Children.Length),
    };

    /// <summary>Query terms separated by set operators, then ORDER BY / OFFSET / FOR / OPTION.</summary>
    private Doc Query(SyntaxNode query)
    {
        var parts = new List<Doc>();
        var started = false;
        var previousWasToken = false;
        foreach (var child in query.Children)
        {
            switch (child)
            {
                case SyntaxToken token:
                    if (started && !previousWasToken)
                    {
                        parts.Add(ClauseBreak);
                    }

                    parts.Add(Emit(token));
                    previousWasToken = true;
                    break;
                case SyntaxNode node:
                    if (started)
                    {
                        parts.Add(ClauseBreak);
                    }

                    parts.Add(node.Kind == SyntaxKind.QuerySpecification || ClauseKinds.Contains(node.Kind) ? Clause(node) : InlineElement(node));
                    previousWasToken = false;
                    break;
            }

            started = true;
        }

        return Doc.Concat(parts);
    }

    private Doc From(SyntaxNode from)
    {
        var parts = new List<Doc>();
        foreach (var child in from.Children)
        {
            if (child is SyntaxNode { Kind: SyntaxKind.JoinClause } join)
            {
                var line = Doc.Concat(ClauseBreak, Join(join));
                parts.Add(_profile.IndentJoins ? Doc.Nest(line) : line);
            }
            else
            {
                parts.Add(InlineElement(child));
            }
        }

        return Doc.Concat(parts);
    }

    private Doc Join(SyntaxNode join)
    {
        var parts = new List<Doc>();
        foreach (var child in join.Children)
        {
            parts.Add(child is SyntaxNode { Kind: SyntaxKind.OnClause } on ? KeywordThenPredicate(on) : InlineElement(child));
        }

        return Doc.Concat(parts);
    }

    /// <summary>WITH a AS (…), b AS (…): one common table expression per line.</summary>
    private Doc With(SyntaxNode with)
    {
        var parts = new List<Doc>();
        foreach (var child in with.Children)
        {
            if (child is SyntaxToken { Kind: SyntaxKind.CommaToken } comma)
            {
                if (_profile.Commas == CommaPlacement.Leading)
                {
                    parts.Add(Doc.HardLine);
                    parts.Add(Emit(comma));
                }
                else
                {
                    parts.Add(Emit(comma));
                    parts.Add(Doc.HardLine);
                }
            }
            else
            {
                parts.Add(InlineElement(child));
            }
        }

        return Doc.Concat(parts);
    }

    /// <summary>Leading tokens (keywords, TOP, DISTINCT), then the expression as a comma list.</summary>
    private Doc KeywordThenList(SyntaxNode node) => KeywordThen(node, (children, start, end) => List(children, start, end));

    private Doc KeywordThenPredicate(SyntaxNode node) => KeywordThen(node, Predicate);

    private Doc KeywordThen(SyntaxNode node, Func<ImmutableArray<SyntaxElement>, int, int, Doc> layout)
    {
        var parts = new List<Doc>();
        foreach (var child in node.Children)
        {
            parts.Add(child is SyntaxNode { Kind: SyntaxKind.Expression } expression
                ? layout(expression.Children, 0, expression.Children.Length)
                : InlineElement(child));
        }

        return Doc.Concat(parts);
    }

    /// <summary>VALUES (…), (…): the first child is the keyword, the rest a comma list.</summary>
    private Doc ListAfterFirst(SyntaxNode node) =>
        Doc.Concat(InlineElement(node.Children[0]), List(node.Children, 1, node.Children.Length));

    // ───────────────────────────── lists, predicates, CASE ─────────────────────────────

    /// <summary>
    /// Elements split at top-level commas. One item stays inline; several form a group that is either flat
    /// (<c>a, b, c</c>) or one item per line, indented.
    /// </summary>
    private Doc List(ImmutableArray<SyntaxElement> children, int start, int end, bool parenthesised = false)
    {
        var items = new List<(int Start, int End)>();
        var commas = new List<SyntaxToken>();
        var itemStart = start;
        for (var i = start; i < end; i++)
        {
            if (children[i] is SyntaxToken { Kind: SyntaxKind.CommaToken } comma)
            {
                items.Add((itemStart, i));
                commas.Add(comma);
                itemStart = i + 1;
            }
        }

        items.Add((itemStart, end));
        if (commas.Count == 0)
        {
            return InlineChildren(children, start, end);
        }

        var body = new List<Doc> { parenthesised ? Doc.SoftLine : Doc.Line };
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                var comma = commas[i - 1];
                if (_profile.Commas == CommaPlacement.Leading)
                {
                    body.Add(Doc.SoftLine); // flat: "a, b"; broken: ", b" starts the line
                    body.Add(Emit(comma));
                    body.Add(Doc.Space);
                }
                else if (EndsWithLineComment(_tokens[_index[comma] - 1]))
                {
                    body.Add(Emit(comma)); // "a -- note" ends the line, so the comma leads the next: ", b"
                    body.Add(Doc.Space);
                }
                else
                {
                    body.Add(Emit(comma));
                    body.Add(Doc.Line);
                }
            }

            body.Add(InlineChildren(children, items[i].Start, items[i].End));
        }

        // A parenthesised list shares one group with its closing parenthesis, so both break together.
        var nested = Doc.Nest(Doc.Concat(body));
        return parenthesised ? nested : Doc.Group(nested);
    }

    /// <summary>Boolean conditions split before top-level AND / OR (not the AND of BETWEEN, not inside CASE).</summary>
    private Doc Predicate(ImmutableArray<SyntaxElement> children, int start, int end)
    {
        var parts = new List<Doc>();
        var segmentStart = start;
        var caseDepth = 0;
        var betweenPending = false;
        for (var i = start; i < end; i++)
        {
            if (children[i] is not SyntaxToken token)
            {
                continue;
            }

            if (IsWord(token, "CASE"))
            {
                caseDepth++;
            }
            else if (IsWord(token, "END") && caseDepth > 0)
            {
                caseDepth--;
            }
            else if (caseDepth == 0 && IsWord(token, "BETWEEN"))
            {
                betweenPending = true;
            }
            else if (caseDepth == 0 && token.Kind == SyntaxKind.KeywordToken && (IsWord(token, "AND") || IsWord(token, "OR")))
            {
                if (betweenPending && IsWord(token, "AND"))
                {
                    betweenPending = false;
                    continue;
                }

                parts.Add(InlineChildren(children, segmentStart, i));
                parts.Add(Doc.Line);
                segmentStart = i;
            }
        }

        parts.Add(InlineChildren(children, segmentStart, end));
        return parts.Count == 1 ? parts[0] : Doc.Group(Doc.Nest(Doc.Concat(parts)));
    }

    /// <summary>CASE [input] WHEN … THEN … [ELSE …] END: flat when it fits, else one branch per line.</summary>
    private Doc Case(ImmutableArray<SyntaxElement> children, int caseIndex, int endIndex)
    {
        var branches = new List<int>();
        var depth = 0;
        for (var i = caseIndex + 1; i < endIndex; i++)
        {
            if (children[i] is not SyntaxToken token)
            {
                continue;
            }

            if (IsWord(token, "CASE"))
            {
                depth++;
            }
            else if (IsWord(token, "END") && depth > 0)
            {
                depth--;
            }
            else if (depth == 0 && (IsWord(token, "WHEN") || IsWord(token, "ELSE")))
            {
                branches.Add(i);
            }
        }

        var headerEnd = branches.Count > 0 ? branches[0] : endIndex;
        var body = new List<Doc>();
        for (var b = 0; b < branches.Count; b++)
        {
            body.Add(Doc.Line);
            body.Add(Predicate(children, branches[b], b + 1 < branches.Count ? branches[b + 1] : endIndex)); // WHEN a AND b breaks before AND
        }

        return Doc.Group(Doc.Concat(
            Emit((SyntaxToken)children[caseIndex]),
            InlineChildren(children, caseIndex + 1, headerEnd),
            Doc.Nest(Doc.Concat(body)),
            Doc.Line,
            Emit((SyntaxToken)children[endIndex])));
    }

    // ───────────────────────────── inline elements ─────────────────────────────

    private Doc InlineChildren(ImmutableArray<SyntaxElement> children, int start, int end)
    {
        var parts = new List<Doc>(end - start);
        for (var i = start; i < end; i++)
        {
            if (children[i] is SyntaxToken token && IsWord(token, "CASE") && FindCaseEnd(children, i, end) is { } caseEnd)
            {
                parts.Add(Case(children, i, caseEnd));
                i = caseEnd;
                continue;
            }

            parts.Add(InlineElement(children[i]));
        }

        return Doc.Concat(parts);
    }

    private static int? FindCaseEnd(ImmutableArray<SyntaxElement> children, int caseIndex, int end)
    {
        var depth = 0;
        for (var i = caseIndex; i < end; i++)
        {
            if (children[i] is SyntaxToken token)
            {
                if (IsWord(token, "CASE"))
                {
                    depth++;
                }
                else if (IsWord(token, "END") && --depth == 0)
                {
                    return i;
                }
            }
        }

        return null;
    }

    private Doc InlineElement(SyntaxElement element)
    {
        if (element is SyntaxToken token)
        {
            return Emit(token);
        }

        var node = (SyntaxNode)element;
        return node.Kind switch
        {
            SyntaxKind.Subquery => Subquery(node),
            SyntaxKind.ParenthesizedExpression => Parenthesized(node),
            SyntaxKind.QueryExpression => Doc.Nest(Doc.Concat(Doc.HardLine, Query(node))),
            _ when StatementKinds.Contains(node.Kind) => Statement(node),
            _ when ClauseKinds.Contains(node.Kind) => Doc.Nest(Doc.Concat(Doc.HardLine, Clause(node))),
            _ => InlineChildren(node.Children, 0, node.Children.Length),
        };
    }

    /// <summary>( query ): the query on its own lines, indented, unless short queries collapse.</summary>
    private Doc Subquery(SyntaxNode node)
    {
        var children = node.Children;
        if (children.Length < 2 || children[0] is not SyntaxToken { Kind: SyntaxKind.OpenParenToken } open
            || children[1] is not SyntaxNode { Kind: SyntaxKind.QueryExpression } query)
        {
            return InlineChildren(children, 0, children.Length);
        }

        var parts = new List<Doc> { Emit(open), Doc.Nest(Doc.Concat(Doc.SoftLine, Query(query))) };
        if (children.Length > 2)
        {
            parts.Add(Doc.SoftLine);
            parts.Add(InlineChildren(children, 2, children.Length));
        }

        return Doc.Group(Doc.Concat(parts));
    }

    /// <summary>( a, b ): flat when it fits, else one item per line; without top-level commas always flat.</summary>
    private Doc Parenthesized(SyntaxNode node)
    {
        var children = node.Children;
        var closes = children[^1] is SyntaxToken { Kind: SyntaxKind.CloseParenToken } && children.Length > 1;
        var innerEnd = closes ? children.Length - 1 : children.Length;
        var hasComma = false;
        for (var i = 1; i < innerEnd; i++)
        {
            hasComma |= children[i] is SyntaxToken { Kind: SyntaxKind.CommaToken };
        }

        if (!hasComma)
        {
            return InlineChildren(children, 0, children.Length);
        }

        var parts = new List<Doc> { InlineElement(children[0]), List(children, 1, innerEnd, parenthesised: true) };
        if (closes)
        {
            parts.Add(Doc.SoftLine);
            parts.Add(Emit((SyntaxToken)children[^1]));
        }

        return Doc.Group(Doc.Concat(parts));
    }

    // ───────────────────────────── tokens and trivia ─────────────────────────────

    /// <summary>
    /// The node's original text from its first to its last token — whitespace and comments untouched — with keywords
    /// cased like the rest of the document. Comments around it are handled as usual.
    /// </summary>
    private Doc VerbatimNode(SyntaxNode node)
    {
        var first = node.FirstToken;
        var last = node.LastToken;
        var text = new System.Text.StringBuilder(last.Span.End - first.Position);
        var position = first.Position;
        foreach (var token in node.DescendantTokens())
        {
            text.Append(_text, position, token.Position - position);
            text.Append(Cased(token));
            position = token.Span.End;
        }

        return Doc.Concat(Separator(first), Leading(first), Doc.Verbatim(text.ToString()), Trailing(last));
    }

    private Doc Emit(SyntaxToken token) =>
        Doc.Concat(Separator(token), Leading(token), Doc.Verbatim(Cased(token)), Trailing(token));

    private string Cased(SyntaxToken token) => token.Kind != SyntaxKind.KeywordToken
        ? token.Text
        : _profile.Keywords switch
        {
            KeywordCasing.Upper => token.Text.ToUpperInvariant(),
            KeywordCasing.Lower => token.Text.ToLowerInvariant(),
            _ => token.Text,
        };

    /// <summary>Space between the source-previous token and this one.</summary>
    private Doc Separator(SyntaxToken next)
    {
        var index = _index[next];
        if (index == 0)
        {
            return Doc.Empty;
        }

        var previous = _tokens[index - 1];
        var hadGap = !previous.TrailingTrivia.IsEmpty || !next.LeadingTrivia.IsEmpty;
        var before = previous.Kind;
        var after = next.Kind;

        if (after is SyntaxKind.CommaToken or SyntaxKind.SemicolonToken or SyntaxKind.CloseParenToken or SyntaxKind.DotToken
                or SyntaxKind.ColonColonToken or SyntaxKind.ColonToken or SyntaxKind.EndOfFileToken
            || before is SyntaxKind.OpenParenToken or SyntaxKind.DotToken or SyntaxKind.ColonColonToken)
        {
            return Doc.Empty;
        }

        if (before == SyntaxKind.CommaToken || IsSpacedOperator(before) || IsSpacedOperator(after))
        {
            return Doc.Space;
        }

        if (after == SyntaxKind.OpenParenToken && before == SyntaxKind.KeywordToken)
        {
            return FunctionKeywords.Contains(previous.Text) ? Doc.Empty : Doc.Space;
        }

        return hadGap ? Doc.Space : Doc.Empty;
    }

    private static bool IsSpacedOperator(SyntaxKind kind) => kind is
        SyntaxKind.EqualsToken or SyntaxKind.NotEqualsToken or SyntaxKind.LessThanToken or SyntaxKind.LessThanEqualsToken
        or SyntaxKind.GreaterThanToken or SyntaxKind.GreaterThanEqualsToken or SyntaxKind.NotLessThanToken
        or SyntaxKind.NotGreaterThanToken or SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken
        or SyntaxKind.AsteriskEqualsToken or SyntaxKind.SlashEqualsToken or SyntaxKind.PercentEqualsToken
        or SyntaxKind.AmpersandEqualsToken or SyntaxKind.BarEqualsToken or SyntaxKind.BarBarEqualsToken
        or SyntaxKind.CaretEqualsToken;

    /// <summary>
    /// Comments before the token. A comment that was on its own line stays on its own line; blank lines are kept
    /// (at most <see cref="StyleProfile.MaxBlankLines"/>) only before a statement's first token.
    /// </summary>
    private Doc Leading(SyntaxToken token)
    {
        var index = _index[token];
        var newLines = index == 0 ? 0 : CountLineBreaks(_tokens[index - 1].TrailingTrivia);
        var parts = new List<Doc>();
        var sawComment = false;
        var lastWasLineComment = false;
        foreach (var trivia in token.LeadingTrivia)
        {
            if (trivia.Kind == SyntaxKind.EndOfLineTrivia)
            {
                newLines++;
            }
            else if (SyntaxFacts.IsComment(trivia.Kind))
            {
                parts.Add(BreakFor(newLines, token));
                parts.Add(Doc.Verbatim(trivia.Text));
                sawComment = true;
                lastWasLineComment = trivia.Kind == SyntaxKind.SingleLineCommentTrivia;
                newLines = 0;
            }
        }

        if (sawComment)
        {
            parts.Add(BreakFor(lastWasLineComment ? Math.Max(1, newLines) : newLines, token));
        }
        else if (ReferenceEquals(token, _blankAllowedFor) && newLines >= 2 && _profile.MaxBlankLines > 0)
        {
            parts.Add(Doc.BlankLine);
        }

        return Doc.Concat(parts);
    }

    private Doc BreakFor(int newLines, SyntaxToken token) => newLines switch
    {
        0 => Doc.Space,
        1 => Doc.HardLine,
        _ => ReferenceEquals(token, _blankAllowedFor) && _profile.MaxBlankLines > 0 ? Doc.BlankLine : Doc.HardLine,
    };

    /// <summary>Comments after the token on the same line; a line comment ends the line.</summary>
    private static Doc Trailing(SyntaxToken token)
    {
        var parts = new List<Doc>();
        foreach (var trivia in token.TrailingTrivia)
        {
            if (SyntaxFacts.IsComment(trivia.Kind))
            {
                parts.Add(Doc.Space);
                parts.Add(Doc.Verbatim(trivia.Text));
                if (trivia.Kind == SyntaxKind.SingleLineCommentTrivia)
                {
                    parts.Add(Doc.HardLine);
                }
            }
        }

        return Doc.Concat(parts);
    }

    private static bool EndsWithLineComment(SyntaxToken token) =>
        token.TrailingTrivia.Any(t => t.Kind == SyntaxKind.SingleLineCommentTrivia);

    private static int CountLineBreaks(ImmutableArray<SyntaxTrivia> trivia)
    {
        var count = 0;
        foreach (var item in trivia)
        {
            count += item.Kind == SyntaxKind.EndOfLineTrivia ? 1 : 0;
        }

        return count;
    }

    private static bool IsWord(SyntaxToken? token, string word) =>
        token is { Kind: SyntaxKind.KeywordToken or SyntaxKind.IdentifierToken } && token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);
}
