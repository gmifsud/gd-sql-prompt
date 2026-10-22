using System.Collections.Frozen;
using System.Collections.Immutable;

namespace SqlCompanion.Engine.Syntax;

/// <summary>
/// Error-tolerant recursive-descent parser for the high-traffic T-SQL subset (ADR-0002).
/// </summary>
/// <remarks>
/// <para>Structure is parsed for batches, control flow, modules, DML and queries (clauses, table
/// sources, joins, CTEs, subqueries). Expressions are parsed shallowly: a run of tokens with
/// balanced parentheses and <c>CASE … END</c> nesting, in which nested queries are recognised.
/// Everything else becomes <see cref="SyntaxKind.OpaqueStatement"/> or a simple statement whose
/// text is preserved verbatim.</para>
/// <para>Invariants: never throws; every token (including end-of-file) is placed in the tree exactly
/// once, in order; every loop consumes at least one token per iteration or exits.</para>
/// <para>T-SQL statements need not be terminated, so statement boundaries are found by recognising
/// statement-starting keywords in context (<see cref="IsStatementStart"/>).</para>
/// </remarks>
internal sealed class Parser
{
    private static readonly FrozenSet<string> StatementStarters = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "DECLARE", "SET", "IF", "WHILE", "BEGIN", "RETURN",
        "PRINT", "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "TRUNCATE", "USE", "GOTO", "RAISERROR",
        "BREAK", "CONTINUE", "COMMIT", "ROLLBACK", "SAVE", "GRANT", "DENY", "REVOKE", "FETCH", "OPEN",
        "CLOSE", "DEALLOCATE", "WAITFOR", "BACKUP", "RESTORE", "DBCC", "KILL", "CHECKPOINT", "RECONFIGURE",
        "SHUTDOWN", "BULK", "SETUSER", "REVERT", "READTEXT", "WRITETEXT", "UPDATETEXT");

    /// <summary>A starter keyword preceded by one of these is part of the current statement (GRANT SELECT, FOR INSERT, INSTEAD OF UPDATE, …).</summary>
    /// <remarks><c>ON</c> is deliberately absent: <c>SET NOCOUNT ON</c> is routinely followed by a statement without a terminator.</remarks>
    private static readonly FrozenSet<string> StarterSuppressingPredecessors = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "FOR", "AFTER", "OF", "OR", "GRANT", "DENY", "REVOKE", "BULK", "INSTEAD", "NOT");

    /// <summary>Object-type words that precede <c>IF EXISTS</c> in <c>DROP … IF EXISTS</c>.</summary>
    private static readonly FrozenSet<string> DropObjectTypes = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "TABLE", "VIEW", "PROCEDURE", "PROC", "FUNCTION", "INDEX", "TRIGGER", "SCHEMA", "DATABASE", "TYPE",
        "SEQUENCE", "SYNONYM", "USER", "ROLE", "DEFAULT", "RULE", "ASSEMBLY", "STATISTICS", "COLUMN",
        "CONSTRAINT", "AGGREGATE", "LOGIN", "EVENT", "SESSION", "SECURITY", "POLICY", "EXTERNAL", "MASTER", "KEY");

    private static readonly FrozenSet<string> QueryClauseStops = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "INTO", "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "UNION", "EXCEPT", "INTERSECT", "OPTION", "FOR");

    private static readonly FrozenSet<string> OrderByStops = QueryClauseStops.Append("OFFSET").ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> JoinWords = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER");

    private static readonly FrozenSet<string> JoinHints = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "LOOP", "HASH", "MERGE", "REMOTE");

    private static readonly FrozenSet<string> OnConditionStops = QueryClauseStops
        .Concat(JoinWords).Append("ON").ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SetClauseStops = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase, "OUTPUT", "FROM", "WHERE", "OPTION");

    private static readonly FrozenSet<string> OutputClauseStops = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase, "INTO", "FROM", "WHERE", "VALUES", "OPTION", "DEFAULT");

    private static readonly FrozenSet<string> MergeOnStops = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "WHEN");

    private static readonly FrozenSet<string> MergeWhenStops = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "THEN");

    private static readonly FrozenSet<string> MergeSetStops = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase, "WHEN", "OUTPUT", "OPTION");

    /// <summary>Non-reserved words that cannot be a table alias because they continue the statement.</summary>
    private static readonly FrozenSet<string> NonAliasWords = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase, "APPLY", "OUTPUT", "USING", "WINDOW", "OFFSET");

    /// <summary>
    /// Maximum structural nesting (parentheses, nested statements). Deeper input is kept as flat tokens so
    /// adversarial nesting cannot overflow the stack; SQL Server itself rejects far shallower nesting.
    /// </summary>
    private const int MaxNestingDepth = 128;

    private readonly ImmutableArray<SyntaxToken> _tokens;
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
    private readonly Stack<(SyntaxKind Kind, List<SyntaxElement> Children)> _open = new();
    private int _position;
    private int _depth;
    private string? _statementHead;

    private Parser(ImmutableArray<SyntaxToken> tokens) => _tokens = tokens;

    public static (SyntaxNode Root, ImmutableArray<Diagnostic> Diagnostics) Parse(ImmutableArray<SyntaxToken> tokens)
    {
        var parser = new Parser(tokens);
        var root = parser.ParseCompilationUnit();
        return (root, parser._diagnostics.ToImmutable());
    }

    // ───────────────────────────── tree building ─────────────────────────────

    private void Start(SyntaxKind kind) => _open.Push((kind, []));

    /// <summary>Closes the current node. Nodes that received no tokens are discarded.</summary>
    private void Finish()
    {
        var (kind, children) = _open.Pop();
        if (children.Count == 0)
        {
            return;
        }

        _open.Peek().Children.Add(new SyntaxNode(kind, [.. children]));
    }

    private void Bump()
    {
        if (Current.Kind == SyntaxKind.EndOfFileToken)
        {
            return;
        }

        _open.Peek().Children.Add(Current);
        _position++;
    }

    private SyntaxNode ParseCompilationUnit()
    {
        _open.Push((SyntaxKind.CompilationUnit, []));
        while (!AtEnd)
        {
            if (IsBatchSeparatorAt(_position))
            {
                Start(SyntaxKind.BatchSeparator);
                Bump();
                if (Current.Kind == SyntaxKind.NumericLiteralToken && !IsAtLineStart(_position))
                {
                    Bump();
                }

                Finish();
                continue;
            }

            Start(SyntaxKind.Batch);
            ParseStatementList(insideBlock: false);
            Finish();
        }

        var (kind, children) = _open.Pop();
        children.Add(Current); // end-of-file token owns trailing trivia
        return new SyntaxNode(kind, [.. children]);
    }

    // ───────────────────────────── token helpers ─────────────────────────────

    private SyntaxToken Current => _tokens[_position];

    private SyntaxToken PeekToken(int offset) => TokenAt(_position + offset);

    private SyntaxToken TokenAt(int index) => _tokens[Math.Clamp(index, 0, _tokens.Length - 1)];

    private SyntaxToken? Previous => _position > 0 ? _tokens[_position - 1] : null;

    private bool AtEnd => Current.Kind == SyntaxKind.EndOfFileToken;

    private bool At(SyntaxKind kind) => Current.Kind == kind;

    private bool At(string word) => IsWord(Current, word);

    private static bool IsWord(SyntaxToken? token, string word) =>
        token is { Kind: SyntaxKind.KeywordToken or SyntaxKind.IdentifierToken } && token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsWordIn(SyntaxToken? token, FrozenSet<string> words) =>
        token is { Kind: SyntaxKind.KeywordToken or SyntaxKind.IdentifierToken } && words.Contains(token.Text);

    private static bool IsNamePart(SyntaxToken token) =>
        token.Kind is SyntaxKind.IdentifierToken or SyntaxKind.QuotedIdentifierToken;

    private bool Eat(string word)
    {
        if (!At(word))
        {
            return false;
        }

        Bump();
        return true;
    }

    private void EatSemicolon()
    {
        if (At(SyntaxKind.SemicolonToken))
        {
            Bump();
        }
    }

    private void ExpectCloseParen()
    {
        if (At(SyntaxKind.CloseParenToken))
        {
            Bump();
            return;
        }

        _diagnostics.Add(new Diagnostic(
            SyntaxDiagnosticIds.ExpectedCloseParen,
            DiagnosticSeverity.Error,
            new TextSpan(Current.Position, 0),
            "Expected ')'."));
    }

    // ───────────────────────────── boundaries ─────────────────────────────

    /// <summary><c>GO</c> alone on a line (optionally with a repeat count) separates batches.</summary>
    private bool IsBatchSeparatorAt(int index)
    {
        var token = TokenAt(index);
        if (token.Kind != SyntaxKind.IdentifierToken || !token.Text.Equals("GO", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IsAtLineStart(index))
        {
            return false;
        }

        var lastIndex = TokenAt(index + 1).Kind == SyntaxKind.NumericLiteralToken && !IsAtLineStart(index + 1) ? index + 1 : index;
        return TokenAt(lastIndex + 1).Kind == SyntaxKind.EndOfFileToken || IsAtLineStart(lastIndex + 1);
    }

    /// <summary>True when a line break separates the token at <paramref name="index"/> from the previous token.</summary>
    private bool IsAtLineStart(int index)
    {
        if (index <= 0)
        {
            return true;
        }

        static bool HasLineBreak(ImmutableArray<SyntaxTrivia> trivia) => trivia.Any(t => t.Kind == SyntaxKind.EndOfLineTrivia);
        return HasLineBreak(TokenAt(index).LeadingTrivia) || HasLineBreak(TokenAt(index - 1).TrailingTrivia);
    }

    /// <summary>
    /// Whether the token at <paramref name="index"/> begins a new statement. Reserved starter keywords
    /// begin a statement unless context shows they continue the current one.
    /// </summary>
    private bool IsStatementStart(int index)
    {
        var token = TokenAt(index);
        if (IsWord(token, "THROW"))
        {
            // Non-reserved: a statement only when followed by its arguments or a terminator (never valid in an expression).
            return TokenAt(index + 1).Kind is SyntaxKind.NumericLiteralToken or SyntaxKind.VariableToken
                or SyntaxKind.SemicolonToken or SyntaxKind.EndOfFileToken;
        }

        if (token.Kind != SyntaxKind.KeywordToken)
        {
            return false;
        }

        if (IsWord(token, "WITH"))
        {
            return IsCteStart(index);
        }

        if (!StatementStarters.Contains(token.Text))
        {
            return false;
        }

        var previous = index > 0 ? TokenAt(index - 1) : null;
        var next = TokenAt(index + 1);
        var word = token.Text.ToUpperInvariant();

        if (IsWordIn(previous, StarterSuppressingPredecessors))
        {
            return false;
        }

        if (previous?.Kind == SyntaxKind.CommaToken && word is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "EXECUTE" or "EXEC" or "ALTER" or "CREATE")
        {
            return false; // GRANT SELECT, INSERT …; FOR INSERT, UPDATE …
        }

        // Foreign-key referential actions: ON DELETE CASCADE, ON UPDATE NO ACTION, ON DELETE SET NULL.
        if (word is "DELETE" or "UPDATE" && IsWord(previous, "ON") && (IsWord(next, "CASCADE") || IsWord(next, "NO") || IsWord(next, "SET")))
        {
            return false;
        }

        return word switch
        {
            "UPDATE" => next.Kind != SyntaxKind.OpenParenToken, // UPDATE(column) in triggers
            "MERGE" => !IsWord(next, "JOIN"),
            "EXEC" or "EXECUTE" => !IsWord(previous, "WITH"), // WITH EXECUTE AS
            "SET" => !(next.Kind == SyntaxKind.OpenParenToken
                || (IsWord(previous, "DELETE") || IsWord(previous, "UPDATE")) && IsWord(TokenAt(index - 2), "ON")
                || _statementHead == "ALTER DATABASE"),
            "ALTER" => !IsWord(next, "COLUMN"),
            "DROP" => !(IsWord(next, "COLUMN") || IsWord(next, "CONSTRAINT") || IsWord(next, "PERIOD")),
            "FETCH" => !(IsWord(previous, "ROWS") || IsWord(previous, "ROW")),
            "IF" => !(IsWord(next, "EXISTS") && IsWordIn(previous, DropObjectTypes)),
            "INSERT" => !IsWord(previous, "BULK"),
            "BULK" => previous?.Kind != SyntaxKind.OpenParenToken, // OPENROWSET(BULK …)
            _ => true,
        };
    }

    /// <summary><c>WITH name [(cols)] AS (</c> or <c>WITH XMLNAMESPACES (</c>.</summary>
    private bool IsCteStart(int index)
    {
        if (!IsWord(TokenAt(index), "WITH"))
        {
            return false;
        }

        var next = TokenAt(index + 1);
        if (IsWord(next, "XMLNAMESPACES"))
        {
            return TokenAt(index + 2).Kind == SyntaxKind.OpenParenToken;
        }

        if (!IsNamePart(next))
        {
            return false;
        }

        var i = index + 2;
        if (TokenAt(i).Kind == SyntaxKind.OpenParenToken)
        {
            i = SkipBalanced(i);
        }

        return IsWord(TokenAt(i), "AS") && TokenAt(i + 1).Kind == SyntaxKind.OpenParenToken;
    }

    /// <summary>Returns the index just past the parenthesis that closes the one at <paramref name="index"/>.</summary>
    private int SkipBalanced(int index)
    {
        var depth = 0;
        for (var i = index; i < _tokens.Length; i++)
        {
            var kind = _tokens[i].Kind;
            if (kind == SyntaxKind.OpenParenToken)
            {
                depth++;
            }
            else if (kind == SyntaxKind.CloseParenToken && --depth == 0)
            {
                return i + 1;
            }
            else if (kind == SyntaxKind.EndOfFileToken)
            {
                return i;
            }
        }

        return _tokens.Length - 1;
    }

    /// <summary>
    /// Whether the parenthesis at <paramref name="index"/> opens a query: <c>(SELECT</c>, <c>(WITH cte AS (</c>,
    /// or <c>((query) UNION …)</c> / <c>((query))</c>. A group that merely starts with a subquery, such as
    /// <c>((SELECT …), 0)</c> in <c>COALESCE</c> or a VALUES row, is not a query.
    /// </summary>
    private bool IsQueryAfterParen(int index, int depth = 0)
    {
        if (TokenAt(index).Kind != SyntaxKind.OpenParenToken || depth > MaxNestingDepth)
        {
            return false;
        }

        var next = TokenAt(index + 1);
        if (IsWord(next, "SELECT") || IsCteStart(index + 1))
        {
            return true;
        }

        if (next.Kind != SyntaxKind.OpenParenToken || !IsQueryAfterParen(index + 1, depth + 1))
        {
            return false;
        }

        var afterInner = TokenAt(SkipBalanced(index + 1));
        return afterInner.Kind == SyntaxKind.CloseParenToken
            || IsWord(afterInner, "UNION") || IsWord(afterInner, "EXCEPT") || IsWord(afterInner, "INTERSECT");
    }

    // ───────────────────────────── statements ─────────────────────────────

    private void ParseStatementList(bool insideBlock)
    {
        while (!AtEnd && !IsBatchSeparatorAt(_position))
        {
            if (insideBlock && At("END"))
            {
                return;
            }

            var before = _position;
            ParseStatement();
            if (_position == before)
            {
                Start(SyntaxKind.ErrorNode);
                Bump();
                Finish();
            }
        }
    }

    private void ParseStatement()
    {
        if (_depth >= MaxNestingDepth)
        {
            Start(SyntaxKind.OpaqueStatement);
            Bump();
            Finish();
            return;
        }

        _depth++;
        try
        {
            ParseStatementCore();
        }
        finally
        {
            _depth--;
        }
    }

    private void ParseStatementCore()
    {
        _statementHead = null;

        if (At(SyntaxKind.SemicolonToken))
        {
            Start(SyntaxKind.EmptyStatement);
            Bump();
            Finish();
            return;
        }

        if (At(SyntaxKind.IdentifierToken) && PeekToken(1).Kind == SyntaxKind.ColonToken)
        {
            Start(SyntaxKind.LabelStatement);
            Bump();
            Bump();
            Finish();
            return;
        }

        if (At(SyntaxKind.OpenParenToken) && IsQueryAfterParen(_position))
        {
            ParseDmlStatement(SyntaxKind.SelectStatement);
            return;
        }

        var word = Current.Kind is SyntaxKind.KeywordToken or SyntaxKind.IdentifierToken
            ? Current.Text.ToUpperInvariant()
            : string.Empty;

        switch (word)
        {
            case "SELECT":
                ParseDmlStatement(SyntaxKind.SelectStatement);
                return;
            case "WITH" when IsCteStart(_position):
                ParseDmlStatement(KindAfterWithClause());
                return;
            case "INSERT":
                ParseDmlStatement(SyntaxKind.InsertStatement);
                return;
            case "UPDATE" when !IsWord(PeekToken(1), "STATISTICS"):
                ParseDmlStatement(SyntaxKind.UpdateStatement);
                return;
            case "DELETE":
                ParseDmlStatement(SyntaxKind.DeleteStatement);
                return;
            case "MERGE":
                ParseDmlStatement(SyntaxKind.MergeStatement);
                return;
            case "DECLARE":
                ParseSimpleStatement(SyntaxKind.DeclareStatement);
                return;
            case "SET":
                ParseSimpleStatement(SyntaxKind.SetStatement);
                return;
            case "IF":
                ParseIf();
                return;
            case "WHILE":
                ParseWhile();
                return;
            case "BEGIN":
                ParseBegin();
                return;
            case "CREATE" or "ALTER" when IsModuleDefinition():
                ParseModule();
                return;
            case "CREATE" or "ALTER" or "DROP":
                _statementHead = $"{word} {PeekToken(1).Text.ToUpperInvariant()}";
                ParseSimpleStatement(SyntaxKind.SimpleStatement);
                return;
            case "ELSE" or "END":
                Start(SyntaxKind.OpaqueStatement);
                Bump();
                Finish();
                return;
            case "THROW":
                ParseSimpleStatement(SyntaxKind.SimpleStatement);
                return;
        }

        ParseSimpleStatement(Current.Kind == SyntaxKind.KeywordToken && StatementStarters.Contains(Current.Text)
            ? SyntaxKind.SimpleStatement
            : SyntaxKind.OpaqueStatement);
    }

    /// <summary>A leading keyword followed by a shallow expression run up to the next statement.</summary>
    private void ParseSimpleStatement(SyntaxKind kind)
    {
        Start(kind);
        Bump();
        ParseExpression(stops: null, stopAtComma: false);
        EatSemicolon();
        Finish();
    }

    private void ParseIf()
    {
        Start(SyntaxKind.IfStatement);
        Bump();
        ParseExpression(stops: null, stopAtComma: false);
        ParseEmbeddedStatement();
        if (At("ELSE"))
        {
            Start(SyntaxKind.ElseClause);
            Bump();
            ParseEmbeddedStatement();
            Finish();
        }

        Finish();
    }

    private void ParseWhile()
    {
        Start(SyntaxKind.WhileStatement);
        Bump();
        ParseExpression(stops: null, stopAtComma: false);
        ParseEmbeddedStatement();
        Finish();
    }

    /// <summary>The single statement controlled by IF/ELSE/WHILE; absent while the user is still typing.</summary>
    private void ParseEmbeddedStatement()
    {
        if (!AtEnd && !IsBatchSeparatorAt(_position) && !At("ELSE") && !At("END"))
        {
            ParseStatement();
        }
    }

    private void ParseBegin()
    {
        var next = PeekToken(1);
        if (IsWord(next, "TRY"))
        {
            ParseTryCatch();
            return;
        }

        if (IsWord(next, "TRAN") || IsWord(next, "TRANSACTION") || IsWord(next, "DISTRIBUTED")
            || IsWord(next, "DIALOG") || IsWord(next, "CONVERSATION"))
        {
            ParseSimpleStatement(SyntaxKind.SimpleStatement);
            return;
        }

        Start(SyntaxKind.BlockStatement);
        Bump();
        if (Eat("ATOMIC") && At("WITH") && PeekToken(1).Kind == SyntaxKind.OpenParenToken)
        {
            Bump();
            ParseParenthesized();
        }

        ParseStatementList(insideBlock: true);
        if (Eat("END"))
        {
            EatSemicolon();
        }

        Finish();
    }

    private void ParseTryCatch()
    {
        Start(SyntaxKind.TryCatchStatement);
        Bump(); // BEGIN
        Bump(); // TRY
        ParseStatementList(insideBlock: true);
        if (Eat("END"))
        {
            Eat("TRY");
        }

        if (At("BEGIN") && IsWord(PeekToken(1), "CATCH"))
        {
            Bump();
            Bump();
            ParseStatementList(insideBlock: true);
            if (Eat("END"))
            {
                Eat("CATCH");
            }
        }

        EatSemicolon();
        Finish();
    }

    private bool IsModuleDefinition()
    {
        var i = _position + 1;
        if (IsWord(TokenAt(i), "OR") && IsWord(TokenAt(i + 1), "ALTER"))
        {
            i += 2;
        }

        var kind = TokenAt(i);
        return IsWord(kind, "PROC") || IsWord(kind, "PROCEDURE") || IsWord(kind, "FUNCTION")
            || IsWord(kind, "VIEW") || IsWord(kind, "TRIGGER");
    }

    /// <summary>Procedures, functions, views and triggers: header up to <c>AS</c>, then statements to the end of the batch.</summary>
    private void ParseModule()
    {
        Start(SyntaxKind.CreateModuleStatement);
        Start(SyntaxKind.ModuleHeader);
        while (!AtEnd && !IsBatchSeparatorAt(_position))
        {
            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
                continue;
            }

            if (At("AS") && Previous is { } previous
                && previous.Kind != SyntaxKind.VariableToken && !IsWord(previous, "EXECUTE") && !IsWord(previous, "EXEC"))
            {
                Bump();
                break;
            }

            Bump();
        }

        Finish();
        ParseStatementList(insideBlock: false);
        Finish();
    }

    // ───────────────────────────── DML ─────────────────────────────

    private SyntaxKind KindAfterWithClause()
    {
        var i = _position + 1;
        while (i < _tokens.Length - 1)
        {
            var token = _tokens[i];
            if (token.Kind == SyntaxKind.OpenParenToken)
            {
                i = SkipBalanced(i);
                continue;
            }

            if (token.Kind is SyntaxKind.SemicolonToken or SyntaxKind.EndOfFileToken || IsBatchSeparatorAt(i))
            {
                break;
            }

            if (_tokens[i - 1].Kind == SyntaxKind.CloseParenToken)
            {
                switch (token.Text.ToUpperInvariant())
                {
                    case "SELECT": return SyntaxKind.SelectStatement;
                    case "INSERT": return SyntaxKind.InsertStatement;
                    case "UPDATE": return SyntaxKind.UpdateStatement;
                    case "DELETE": return SyntaxKind.DeleteStatement;
                    case "MERGE": return SyntaxKind.MergeStatement;
                }
            }

            i++;
        }

        return SyntaxKind.SelectStatement;
    }

    private void ParseDmlStatement(SyntaxKind kind)
    {
        Start(kind);
        if (At("WITH"))
        {
            ParseWithClause();
        }

        switch (kind)
        {
            case SyntaxKind.InsertStatement:
                ParseInsertBody();
                break;
            case SyntaxKind.UpdateStatement:
                ParseUpdateBody();
                break;
            case SyntaxKind.DeleteStatement:
                ParseDeleteBody();
                break;
            case SyntaxKind.MergeStatement:
                ParseMergeBody();
                break;
            default:
                ParseQueryExpression();
                break;
        }

        EatSemicolon();
        Finish();
    }

    private void ParseWithClause()
    {
        Start(SyntaxKind.WithClause);
        Bump(); // WITH
        if (At("XMLNAMESPACES"))
        {
            Bump();
            ParseParenthesized();
            if (!At(SyntaxKind.CommaToken))
            {
                Finish();
                return;
            }

            Bump();
        }

        while (IsNamePart(Current))
        {
            Start(SyntaxKind.CommonTableExpression);
            Bump();
            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
            }

            Eat("AS");
            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
            }

            Finish();
            if (!At(SyntaxKind.CommaToken))
            {
                break;
            }

            Bump();
        }

        Finish();
    }

    private void ParseInsertBody()
    {
        Bump(); // INSERT
        ParseTopClause();
        Eat("INTO");
        ParseDmlTarget(allowAlias: false);

        if (At(SyntaxKind.OpenParenToken) && !IsQueryAfterParen(_position))
        {
            Start(SyntaxKind.InsertColumnList);
            ParseParenthesized();
            Finish();
        }

        ParseOutputClause();

        if (At("VALUES"))
        {
            ParseValuesClause();
        }
        else if (At("SELECT") || At("WITH") || (At(SyntaxKind.OpenParenToken) && IsQueryAfterParen(_position)))
        {
            ParseQueryExpression();
        }
        else if (At("EXEC") || At("EXECUTE"))
        {
            ParseSimpleStatement(SyntaxKind.SimpleStatement);
        }
        else if (At("DEFAULT") && IsWord(PeekToken(1), "VALUES"))
        {
            Bump();
            Bump();
        }
    }

    private void ParseValuesClause()
    {
        Start(SyntaxKind.ValuesClause);
        Bump(); // VALUES
        while (At(SyntaxKind.OpenParenToken))
        {
            ParseParenthesized();
            if (!At(SyntaxKind.CommaToken))
            {
                break;
            }

            Bump();
        }

        Finish();
    }

    private void ParseUpdateBody()
    {
        Bump(); // UPDATE
        ParseTopClause();
        ParseDmlTarget(allowAlias: false);
        if (At("SET"))
        {
            Start(SyntaxKind.SetClause);
            Bump();
            ParseExpression(SetClauseStops, stopAtComma: false);
            Finish();
        }

        ParseDmlTail();
    }

    private void ParseDeleteBody()
    {
        Bump(); // DELETE
        ParseTopClause();
        Eat("FROM");
        ParseDmlTarget(allowAlias: false);
        ParseDmlTail();
    }

    /// <summary>OUTPUT, FROM, WHERE and OPTION clauses shared by UPDATE and DELETE.</summary>
    private void ParseDmlTail()
    {
        ParseOutputClause();
        if (At("FROM"))
        {
            ParseFromClause();
        }

        if (At("WHERE"))
        {
            ParseKeywordClause(SyntaxKind.WhereClause, secondWord: null, QueryClauseStops);
        }

        if (At("OPTION"))
        {
            ParseOptionClause();
        }
    }

    private void ParseMergeBody()
    {
        Bump(); // MERGE
        ParseTopClause();
        Eat("INTO");
        ParseDmlTarget(allowAlias: true);

        if (At("USING"))
        {
            Start(SyntaxKind.MergeUsingClause);
            Bump();
            ParseTableSource(isTarget: false);
            Finish();
        }

        if (At("ON"))
        {
            Start(SyntaxKind.OnClause);
            Bump();
            ParseExpression(MergeOnStops, stopAtComma: false);
            Finish();
        }

        while (At("WHEN"))
        {
            Start(SyntaxKind.MergeWhenClause);
            Bump();
            ParseExpression(MergeWhenStops, stopAtComma: false);
            if (Eat("THEN"))
            {
                if (At("UPDATE"))
                {
                    Bump();
                    if (At("SET"))
                    {
                        Start(SyntaxKind.SetClause);
                        Bump();
                        ParseExpression(MergeSetStops, stopAtComma: false);
                        Finish();
                    }
                }
                else if (At("DELETE"))
                {
                    Bump();
                }
                else if (At("INSERT"))
                {
                    Bump();
                    if (At(SyntaxKind.OpenParenToken))
                    {
                        Start(SyntaxKind.InsertColumnList);
                        ParseParenthesized();
                        Finish();
                    }

                    if (At("VALUES"))
                    {
                        ParseValuesClause();
                    }
                    else if (At("DEFAULT") && IsWord(PeekToken(1), "VALUES"))
                    {
                        Bump();
                        Bump();
                    }
                }
            }

            Finish();
        }

        ParseOutputClause();
        if (At("OPTION"))
        {
            ParseOptionClause();
        }
    }

    private void ParseDmlTarget(bool allowAlias)
    {
        Start(SyntaxKind.DmlTarget);
        ParseTableSource(isTarget: true, allowAlias);
        Finish();
    }

    private void ParseTopClause()
    {
        if (!At("TOP"))
        {
            return;
        }

        Start(SyntaxKind.TopClause);
        Bump();
        if (At(SyntaxKind.OpenParenToken))
        {
            ParseParenthesized();
        }
        else if (Current.Kind is SyntaxKind.NumericLiteralToken or SyntaxKind.VariableToken)
        {
            Bump();
        }

        Eat("PERCENT");
        if (At("WITH") && IsWord(PeekToken(1), "TIES"))
        {
            Bump();
            Bump();
        }

        Finish();
    }

    private void ParseOutputClause()
    {
        if (!At("OUTPUT"))
        {
            return;
        }

        Start(SyntaxKind.OutputClause);
        Bump();
        ParseExpression(OutputClauseStops, stopAtComma: false);
        if (At("INTO"))
        {
            Bump();
            ParseTableSource(isTarget: true, allowAlias: false);
            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
            }
        }

        Finish();
    }

    private void ParseOptionClause()
    {
        Start(SyntaxKind.OptionClause);
        Bump();
        if (At(SyntaxKind.OpenParenToken))
        {
            ParseParenthesized();
        }

        Finish();
    }

    // ───────────────────────────── queries ─────────────────────────────

    private void ParseQueryExpression()
    {
        Start(SyntaxKind.QueryExpression);
        ParseQueryTerm();
        while (At("UNION") || At("EXCEPT") || At("INTERSECT"))
        {
            Bump();
            if (!Eat("ALL"))
            {
                Eat("DISTINCT");
            }

            ParseQueryTerm();
        }

        // Clauses that follow a parenthesised term or a set operation.
        ParseQueryTailClauses();
        Finish();
    }

    private void ParseQueryTerm()
    {
        if (At(SyntaxKind.OpenParenToken))
        {
            ParseParenthesized();
        }
        else if (At("SELECT"))
        {
            ParseQuerySpecification();
        }
        else if (At("VALUES"))
        {
            ParseValuesClause();
        }
    }

    private void ParseQuerySpecification()
    {
        Start(SyntaxKind.QuerySpecification);

        Start(SyntaxKind.SelectClause);
        Bump(); // SELECT
        if (!Eat("ALL"))
        {
            Eat("DISTINCT");
        }

        ParseTopClause();
        ParseExpression(QueryClauseStops, stopAtComma: false);
        Finish();

        while (true)
        {
            if (At("INTO"))
            {
                Start(SyntaxKind.IntoClause);
                Bump();
                if (IsNamePart(Current))
                {
                    ParseMultipartName();
                }

                Finish();
            }
            else if (At("FROM"))
            {
                ParseFromClause();
            }
            else if (At("WHERE"))
            {
                ParseKeywordClause(SyntaxKind.WhereClause, secondWord: null, QueryClauseStops);
            }
            else if (At("GROUP"))
            {
                ParseKeywordClause(SyntaxKind.GroupByClause, "BY", QueryClauseStops);
            }
            else if (At("HAVING"))
            {
                ParseKeywordClause(SyntaxKind.HavingClause, secondWord: null, QueryClauseStops);
            }
            else if (!ParseQueryTailClause())
            {
                break;
            }
        }

        Finish();
    }

    private void ParseQueryTailClauses()
    {
        while (ParseQueryTailClause())
        {
        }
    }

    /// <summary>ORDER BY, OFFSET/FETCH, FOR XML/JSON/BROWSE and OPTION. Returns false if none is present.</summary>
    private bool ParseQueryTailClause()
    {
        if (At("ORDER"))
        {
            ParseKeywordClause(SyntaxKind.OrderByClause, "BY", OrderByStops);
        }
        else if (At("OFFSET") && Current.Kind == SyntaxKind.IdentifierToken)
        {
            ParseKeywordClause(SyntaxKind.OffsetClause, secondWord: null, QueryClauseStops);
        }
        else if (At("FOR") && IsForClauseKind(PeekToken(1)))
        {
            ParseKeywordClause(SyntaxKind.ForClause, secondWord: null, QueryClauseStops);
        }
        else if (At("OPTION"))
        {
            ParseOptionClause();
        }
        else
        {
            return false;
        }

        return true;
    }

    private static bool IsForClauseKind(SyntaxToken token) =>
        IsWord(token, "XML") || IsWord(token, "JSON") || IsWord(token, "BROWSE");

    private void ParseKeywordClause(SyntaxKind kind, string? secondWord, FrozenSet<string> stops)
    {
        Start(kind);
        Bump();
        if (secondWord is not null)
        {
            Eat(secondWord);
        }

        ParseExpression(stops, stopAtComma: false);
        Finish();
    }

    private void ParseFromClause()
    {
        Start(SyntaxKind.FromClause);
        Bump(); // FROM
        ParseTableSourceWithJoins();
        while (At(SyntaxKind.CommaToken))
        {
            Bump();
            ParseTableSourceWithJoins();
        }

        Finish();
    }

    private void ParseTableSourceWithJoins()
    {
        ParseTableSource(isTarget: false);
        while (IsJoinStart())
        {
            Start(SyntaxKind.JoinClause);
            while (IsWordIn(Current, JoinWords) && !At("JOIN") || IsWordIn(Current, JoinHints))
            {
                Bump();
            }

            if (!Eat("JOIN"))
            {
                Eat("APPLY");
            }

            ParseTableSource(isTarget: false);
            if (At("ON"))
            {
                Start(SyntaxKind.OnClause);
                Bump();
                ParseExpression(OnConditionStops, stopAtComma: true);
                Finish();
            }

            Finish();
        }
    }

    private bool IsJoinStart()
    {
        if (!IsWordIn(Current, JoinWords))
        {
            return false;
        }

        // LEFT(…) and RIGHT(…) are string functions.
        return !((At("LEFT") || At("RIGHT")) && PeekToken(1).Kind == SyntaxKind.OpenParenToken);
    }

    private void ParseTableSource(bool isTarget, bool allowAlias = true)
    {
        if (At(SyntaxKind.OpenParenToken))
        {
            if (IsQueryAfterParen(_position) || IsWord(PeekToken(1), "VALUES"))
            {
                Start(SyntaxKind.DerivedTableSource);
                ParseParenthesized();
                ParseAlias();
                if (At(SyntaxKind.OpenParenToken))
                {
                    ParseParenthesized();
                }

                Finish();
            }
            else
            {
                Start(SyntaxKind.ParenthesizedTableSource);
                Bump();
                ParseTableSourceWithJoins();
                ExpectCloseParen();
                ParseAlias();
                Finish();
            }

            return;
        }

        if (At(SyntaxKind.VariableToken))
        {
            Start(SyntaxKind.VariableTableSource);
            Bump();
            if (At(SyntaxKind.DotToken))
            {
                // @xml.nodes('…') AS t(c)
                Bump();
                if (IsNamePart(Current))
                {
                    Bump();
                }

                if (At(SyntaxKind.OpenParenToken))
                {
                    ParseParenthesized();
                }
            }

            if (allowAlias)
            {
                ParseAlias();
            }

            if (At(SyntaxKind.OpenParenToken) && !isTarget)
            {
                ParseParenthesized();
            }

            Finish();
            return;
        }

        var isRowsetKeyword = Current.Kind == SyntaxKind.KeywordToken
            && Current.Text.StartsWith("OPEN", StringComparison.OrdinalIgnoreCase)
            || At("CONTAINSTABLE") || At("FREETEXTTABLE");
        if (!IsNamePart(Current) && !isRowsetKeyword)
        {
            return;
        }

        var afterName = ScanMultipartName(_position);
        var isFunction = TokenAt(afterName).Kind == SyntaxKind.OpenParenToken && (!isTarget || isRowsetKeyword);

        Start(isFunction ? SyntaxKind.FunctionTableSource : SyntaxKind.NamedTableSource);
        ParseMultipartName();
        if (isFunction)
        {
            ParseParenthesized();
        }

        if (At("FOR") && IsWord(PeekToken(1), "SYSTEM_TIME"))
        {
            ParseSystemTime();
        }
        else if (At("FOR") && IsWord(PeekToken(1), "PATH"))
        {
            Bump(); // graph: Table FOR PATH AS alias
            Bump();
        }

        var hasAlias = allowAlias && ParseAlias();
        if (isFunction && At(SyntaxKind.OpenParenToken))
        {
            ParseParenthesized(); // column aliases
        }

        ParseTableHints();
        if (allowAlias && !hasAlias)
        {
            ParseAlias(); // OPENJSON(…) WITH (schema) AS alias
        }

        while (At("PIVOT") || At("UNPIVOT"))
        {
            Bump();
            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
            }

            ParseAlias();
        }

        Finish();
    }

    private void ParseTableHints()
    {
        if (At("WITH") && PeekToken(1).Kind == SyntaxKind.OpenParenToken)
        {
            Bump();
            ParseParenthesized();
        }

        if (At("TABLESAMPLE"))
        {
            Bump();
            Eat("SYSTEM");
            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
            }

            if (Eat("REPEATABLE") && At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
            }
        }
    }

    /// <summary><c>FOR SYSTEM_TIME</c> {AS OF x | FROM x TO y | BETWEEN x AND y | CONTAINED IN (x, y) | ALL}.</summary>
    private void ParseSystemTime()
    {
        Bump(); // FOR
        Bump(); // SYSTEM_TIME
        if (Eat("ALL"))
        {
            return;
        }

        if (At("AS"))
        {
            Bump();
            Eat("OF");
            BumpOperand();
        }
        else if (At("FROM") || At("BETWEEN"))
        {
            Bump();
            BumpOperand();
            if (!Eat("TO"))
            {
                Eat("AND");
            }

            BumpOperand();
        }
        else if (At("CONTAINED"))
        {
            Bump();
            Eat("IN");
            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
            }
        }
    }

    private void BumpOperand()
    {
        if (At(SyntaxKind.OpenParenToken))
        {
            ParseParenthesized();
        }
        else if (Current.Kind is SyntaxKind.StringLiteralToken or SyntaxKind.VariableToken or SyntaxKind.NumericLiteralToken)
        {
            Bump();
        }
    }

    private int ScanMultipartName(int index)
    {
        var i = index + 1;
        while (TokenAt(i).Kind == SyntaxKind.DotToken)
        {
            i++;
            if (TokenAt(i).Kind is SyntaxKind.IdentifierToken or SyntaxKind.QuotedIdentifierToken or SyntaxKind.KeywordToken)
            {
                i++;
            }
        }

        return i;
    }

    /// <summary><c>part(.part)*</c>; empty parts (<c>db..t</c>) and a trailing dot (<c>c.</c> while typing) are allowed.</summary>
    private void ParseMultipartName()
    {
        Start(SyntaxKind.MultipartName);
        Bump();
        while (At(SyntaxKind.DotToken))
        {
            Bump();
            if (Current.Kind is SyntaxKind.IdentifierToken or SyntaxKind.QuotedIdentifierToken
                || Current.Kind == SyntaxKind.KeywordToken && !IsStatementStart(_position))
            {
                Bump();
            }
        }

        Finish();
    }

    private bool ParseAlias()
    {
        if (At("AS"))
        {
            Start(SyntaxKind.Alias);
            Bump();
            if (IsNamePart(Current) || At(SyntaxKind.StringLiteralToken))
            {
                Bump();
            }

            Finish();
            return true;
        }

        if (IsNamePart(Current) && !IsWordIn(Current, NonAliasWords) && !IsBatchSeparatorAt(_position))
        {
            Start(SyntaxKind.Alias);
            Bump();
            Finish();
            return true;
        }

        return false;
    }

    // ───────────────────────────── expressions ─────────────────────────────

    /// <summary>
    /// Parses a parenthesised group: a <see cref="SyntaxKind.Subquery"/> when it opens with a query,
    /// otherwise a <see cref="SyntaxKind.ParenthesizedExpression"/> whose contents are skipped with nesting.
    /// An unclosed group ends at a statement-starting keyword at the beginning of a line.
    /// </summary>
    private void ParseParenthesized()
    {
        if (_depth >= MaxNestingDepth)
        {
            Bump(); // too deep: keep the parenthesis as a flat token
            return;
        }

        _depth++;
        try
        {
            ParseParenthesizedCore();
        }
        finally
        {
            _depth--;
        }
    }

    private void ParseParenthesizedCore()
    {
        if (IsQueryAfterParen(_position))
        {
            Start(SyntaxKind.Subquery);
            Bump();
            ParseQueryExpression();
            ExpectCloseParen();
            Finish();
            return;
        }

        var callee = Previous;
        Start(SyntaxKind.ParenthesizedExpression);
        Bump();
        while (true)
        {
            if (AtEnd || At(SyntaxKind.SemicolonToken) || IsBatchSeparatorAt(_position)
                || IsAtLineStart(_position) && IsStatementStart(_position)
                || IsClauseKeywordOutsideQuery(callee))
            {
                ExpectCloseParen();
                break;
            }

            if (At(SyntaxKind.CloseParenToken))
            {
                Bump();
                break;
            }

            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
                continue;
            }

            Bump();
        }

        Finish();
    }

    /// <summary>
    /// Recovery for an unclosed group while typing (<c>COUNT(</c> followed by <c>FROM t</c>): query clause and
    /// join keywords cannot occur inside a non-query parenthesised group, except <c>FROM</c> in <c>TRIM(… FROM …)</c>.
    /// </summary>
    private bool IsClauseKeywordOutsideQuery(SyntaxToken? callee)
    {
        // OPTION (HASH JOIN, MERGE JOIN, …) query hints legitimately contain join keywords.
        if (Current.Kind != SyntaxKind.KeywordToken || IsWord(callee, "OPTION"))
        {
            return false;
        }

        return Current.Text.ToUpperInvariant() switch
        {
            "FROM" => !IsWord(callee, "TRIM"),
            "WHERE" or "HAVING" or "UNION" or "EXCEPT" or "INTERSECT" or "JOIN" or "INNER" or "CROSS" => true,
            "GROUP" => !IsWord(Previous, "WITHIN"),
            "LEFT" or "RIGHT" or "FULL" or "OUTER" => IsJoinStart() && PeekToken(1).Kind != SyntaxKind.OpenParenToken,
            _ => false,
        };
    }

    /// <summary>
    /// Shallow expression run: consumes tokens until a stop word, a statement start, an unmatched
    /// <c>)</c>, <c>;</c>, a batch separator or end of input. Nested parentheses and queries are parsed.
    /// </summary>
    private void ParseExpression(FrozenSet<string>? stops, bool stopAtComma)
    {
        var caseDepth = 0;
        if (AtExpressionStop(stops, stopAtComma, caseDepth))
        {
            return;
        }

        Start(SyntaxKind.Expression);
        while (!AtExpressionStop(stops, stopAtComma, caseDepth))
        {
            if (At(SyntaxKind.OpenParenToken))
            {
                ParseParenthesized();
            }
            else if (At("CASE"))
            {
                caseDepth++;
                Bump();
            }
            else if (At("END") && caseDepth > 0)
            {
                caseDepth--;
                Bump();
            }
            else if (At("SELECT") && IsWord(Previous, "FOR"))
            {
                ParseQueryExpression(); // DECLARE c CURSOR FOR SELECT …
            }
            else
            {
                Bump();
            }
        }

        Finish();
    }

    private bool AtExpressionStop(FrozenSet<string>? stops, bool stopAtComma, int caseDepth)
    {
        if (AtEnd || At(SyntaxKind.SemicolonToken) || At(SyntaxKind.CloseParenToken) || IsBatchSeparatorAt(_position))
        {
            return true;
        }

        if (stopAtComma && At(SyntaxKind.CommaToken))
        {
            return true;
        }

        if (At("ELSE") || At("END"))
        {
            return caseDepth == 0;
        }

        if (stops is not null && IsWordIn(Current, stops) && IsStopInContext())
        {
            return true;
        }

        return IsStatementStart(_position);
    }

    /// <summary>Context checks for stop words that are also used inside expressions.</summary>
    private bool IsStopInContext()
    {
        if (At("GROUP"))
        {
            return !IsWord(Previous, "WITHIN"); // STRING_AGG(…) WITHIN GROUP (…)
        }

        if (At("FOR"))
        {
            return IsForClauseKind(PeekToken(1));
        }

        if (At("LEFT") || At("RIGHT"))
        {
            return PeekToken(1).Kind != SyntaxKind.OpenParenToken;
        }

        return true;
    }
}
