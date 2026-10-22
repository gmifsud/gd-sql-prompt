using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlCompanion.Engine.Binding;

namespace SqlCompanion.Spikes.S2;

internal sealed record OracleSource(string ExposedName, string Descriptor, int Start, int End);

internal sealed record OracleScope(int Start, int CaretEnd, bool IsMerge, List<(int Start, CompletionClause Clause)> Markers, List<OracleSource> Sources, List<OracleSource> TargetSources)
{
    /// <summary>Starts of this scope's own clause keywords (nested scopes excluded): where a clause being typed ends.</summary>
    public List<int> Boundaries { get; } = [];
}

/// <summary>Expected completion context at a caret, derived from ScriptDom's parse of the complete script.</summary>
internal sealed record Expected(CompletionClause Clause, IReadOnlyList<OracleSource> Sources, IReadOnlyList<OracleSource> TargetSources, int RegionEnd, int StatementEnd);

/// <summary>
/// Reference model built from <c>Microsoft.SqlServer.TransactSql.ScriptDom</c> over a script that parses
/// without errors. Defines, independently of our parser, the clause and visible sources at a caret.
/// </summary>
internal sealed class Oracle
{
    private readonly List<OracleScope> _scopes = [];
    private readonly List<(int Start, int End)> _excluded = [];
    private readonly List<(int Start, int End)> _statements = [];

    private Oracle()
    {
    }

    public List<int> StatementStarts { get; } = [];

    public Dictionary<int, string> StatementTypes { get; } = [];

    public static Oracle? TryBuild(string text, out int errorCount)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(text);
        var fragment = parser.Parse(reader, out var errors);
        errorCount = errors.Count;
        if (errors.Count > 0 || fragment is null)
        {
            return null;
        }

        var oracle = new Oracle();
        fragment.Accept(new Visitor(oracle));
        return oracle;
    }

    /// <summary>Returns the expected context, or null when the caret is outside the compared domain.</summary>
    public Expected? Classify(int position)
    {
        if (_excluded.Any(r => position >= r.Start && position <= r.End))
        {
            return null;
        }

        var scope = _scopes
            .Where(s => position >= s.Start && position <= s.CaretEnd)
            .OrderBy(s => s.CaretEnd - s.Start)
            .FirstOrDefault();
        if (scope is null || scope.IsMerge)
        {
            return null;
        }

        var clause = CompletionClause.None;
        foreach (var (start, kind) in scope.Markers.OrderBy(m => m.Start))
        {
            if (start > position)
            {
                break;
            }

            clause = kind;
        }

        if (clause is CompletionClause.None or CompletionClause.Other)
        {
            return null;
        }

        var regionEnd = scope.Boundaries.Where(b => b > position).DefaultIfEmpty(scope.CaretEnd).Min();
        regionEnd = Math.Min(regionEnd, scope.CaretEnd);
        var statement = _statements.Where(s => position >= s.Start && position <= s.End).OrderBy(s => s.End - s.Start).FirstOrDefault();
        var statementEnd = statement == default ? scope.CaretEnd : statement.End;

        return new Expected(clause, scope.Sources, scope.TargetSources, regionEnd, statementEnd);
    }

    private sealed class Visitor(Oracle oracle) : TSqlFragmentVisitor
    {
        private List<int>? _boundaries;

        /// <summary>Registers a scope; boundaries recorded until the next scope belong to it.</summary>
        private void AddScope(OracleScope scope, List<int> boundaries)
        {
            scope.Boundaries.AddRange(boundaries);
            oracle._scopes.Add(scope);
            _boundaries = null;
        }

        public override void ExplicitVisit(TSqlBatch node)
        {
            RecordStatements(node.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(StatementList node)
        {
            RecordStatements(node.Statements);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(IfStatement node)
        {
            RecordStatement(node.ThenStatement);
            RecordStatement(node.ElseStatement);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(WhileStatement node)
        {
            RecordStatement(node.Statement);
            base.ExplicitVisit(node);
        }

        public override void Visit(TSqlStatement node)
        {
            oracle._statements.Add((node.StartOffset, End(node)));
            base.Visit(node);
        }

        public override void Visit(QueryExpression node)
        {
            // ORDER BY / OFFSET / FOR on a set operation or parenthesised query belong to no query block.
            if (node is not QuerySpecification)
            {
                Exclude(node.OrderByClause);
                Exclude(node.OffsetClause);
                Exclude(node.ForClause);
            }

            base.Visit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            // SELECT … INTO target: the target is not a column-completion position.
            if (node.Into is not null)
            {
                var into = PreviousSignificant(node.Into);
                oracle._excluded.Add((into?.Offset ?? node.Into.StartOffset, End(node.Into)));
                Boundary(into?.Offset ?? node.Into.StartOffset);
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var boundaries = _boundaries = [];
            var markers = new List<(int, CompletionClause)>();
            AddMarker(markers, node.ScriptTokenStream[node.FirstTokenIndex], CompletionClause.SelectList);
            Exclude(node.TopRowFilter);
            var sources = new List<OracleSource>();
            AddFromClause(node.FromClause, markers, sources);
            AddKeywordClause(node.WhereClause, markers, CompletionClause.Where, twoWords: false);
            AddKeywordClause(node.GroupByClause, markers, CompletionClause.GroupBy, twoWords: true);
            AddKeywordClause(node.HavingClause, markers, CompletionClause.Having, twoWords: false);
            AddKeywordClause(node.OrderByClause, markers, CompletionClause.OrderBy, twoWords: true);
            Exclude(node.OffsetClause);
            ExcludeForClause(node.ForClause);
            Exclude(node.WindowClause);

            AddScope(new OracleScope(node.StartOffset, CaretEnd(node), IsMerge: false, markers, sources, []), boundaries);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            var boundaries = _boundaries = [];
            var markers = new List<(int, CompletionClause)>();
            AddMarker(markers, node.ScriptTokenStream[node.FirstTokenIndex], CompletionClause.TableReference);
            Exclude(node.TopRowFilter);
            var targets = TargetSources(node.Target, markers);
            if (node.SetClauses.Count > 0)
            {
                var set = PreviousSignificant(node.SetClauses[0]);
                if (set is not null)
                {
                    AddMarker(markers, set, CompletionClause.SetList);
                }
            }

            ExcludeOutput(node.OutputClause, node.OutputIntoClause);
            var sources = new List<OracleSource>();
            AddFromClause(node.FromClause, markers, sources);
            AddKeywordClause(node.WhereClause, markers, CompletionClause.Where, twoWords: false);
            AddScope(new OracleScope(node.StartOffset, CaretEnd(node), IsMerge: false, markers, [.. targets, .. sources], targets), boundaries);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            var boundaries = _boundaries = [];
            var markers = new List<(int, CompletionClause)>();
            AddMarker(markers, node.ScriptTokenStream[node.FirstTokenIndex], CompletionClause.TableReference);
            Exclude(node.TopRowFilter);
            var targets = TargetSources(node.Target, markers);
            ExcludeOutput(node.OutputClause, node.OutputIntoClause);
            var sources = new List<OracleSource>();
            AddFromClause(node.FromClause, markers, sources);
            AddKeywordClause(node.WhereClause, markers, CompletionClause.Where, twoWords: false);
            AddScope(new OracleScope(node.StartOffset, CaretEnd(node), IsMerge: false, markers, [.. targets, .. sources], targets), boundaries);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(InsertSpecification node)
        {
            var boundaries = _boundaries = [];
            var markers = new List<(int, CompletionClause)>();
            AddMarker(markers, node.ScriptTokenStream[node.FirstTokenIndex], CompletionClause.TableReference);
            Exclude(node.TopRowFilter);
            var targets = TargetSources(node.Target, markers);
            if (node.Columns.Count > 0)
            {
                var open = PreviousSignificant(node.Columns[0]);
                var close = NextSignificant(node.Columns[^1]);
                if (open is not null)
                {
                    AddMarker(markers, open, CompletionClause.InsertColumns);
                }

                if (close is not null)
                {
                    markers.Add((close.Offset + close.Text.Length, CompletionClause.Other));
                    Boundary(close.Offset);
                }
            }

            ExcludeOutput(node.OutputClause, node.OutputIntoClause);
            switch (node.InsertSource)
            {
                case ValuesInsertSource values:
                    AddMarker(markers, values.ScriptTokenStream[values.FirstTokenIndex], CompletionClause.Values);
                    break;
                case SelectInsertSource select:
                    markers.Add((select.StartOffset, CompletionClause.Other));
                    Boundary(select.StartOffset);
                    break;
                case { } other:
                    Exclude(other);
                    break;
            }

            AddScope(new OracleScope(node.StartOffset, CaretEnd(node), IsMerge: false, markers, [], targets), boundaries);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeSpecification node)
        {
            oracle._scopes.Add(new OracleScope(node.StartOffset, CaretEnd(node), IsMerge: true, [], [], []));
            base.ExplicitVisit(node);
        }

        // ── statements ──

        private void RecordStatements(IList<TSqlStatement> statements)
        {
            foreach (var statement in statements)
            {
                RecordStatement(statement);
            }
        }

        private void RecordStatement(TSqlStatement? statement)
        {
            if (statement is null)
            {
                return;
            }

            // ScriptDom's THROW fragment includes the ';' that must precede it; normalise to the first real token.
            var start = statement.StartOffset;
            var tokens = statement.ScriptTokenStream;
            for (var i = statement.FirstTokenIndex; i <= statement.LastTokenIndex; i++)
            {
                if (!IsTrivia(tokens[i]) && tokens[i].TokenType != TSqlTokenType.Semicolon)
                {
                    start = tokens[i].Offset;
                    break;
                }
            }

            oracle.StatementStarts.Add(start);
            oracle.StatementTypes[start] = statement.GetType().Name;
        }

        // ── clauses ──

        private void AddMarker(List<(int, CompletionClause)> markers, TSqlParserToken keyword, CompletionClause clause)
        {
            markers.Add((keyword.Offset + keyword.Text.Length, clause));
            Boundary(keyword.Offset);
        }

        private void AddKeywordClause(TSqlFragment? clause, List<(int, CompletionClause)> markers, CompletionClause kind, bool twoWords)
        {
            if (clause is null)
            {
                return;
            }

            var first = clause.ScriptTokenStream[clause.FirstTokenIndex];
            var head = twoWords ? NextSignificantToken(clause.ScriptTokenStream, clause.FirstTokenIndex) ?? first : first;
            markers.Add((head.Offset + head.Text.Length, kind));
            Boundary(first.Offset);
        }

        private void AddFromClause(FromClause? from, List<(int, CompletionClause)> markers, List<OracleSource> sources)
        {
            if (from is null)
            {
                return;
            }

            AddMarker(markers, from.ScriptTokenStream[from.FirstTokenIndex], CompletionClause.TableReference);
            foreach (var reference in from.TableReferences)
            {
                Collect(reference, markers, sources);
            }
        }

        private void Collect(TableReference reference, List<(int, CompletionClause)> markers, List<OracleSource> sources)
        {
            switch (reference)
            {
                case JoinTableReference join:
                    Collect(join.FirstTableReference, markers, sources);
                    var joinStart = NextSignificant(join.FirstTableReference);
                    var joinWord = PreviousSignificant(join.SecondTableReference);
                    if (joinStart is not null)
                    {
                        Boundary(joinStart.Offset);
                    }

                    if (joinWord is not null)
                    {
                        markers.Add((joinWord.Offset + joinWord.Text.Length, CompletionClause.TableReference));
                    }

                    Collect(join.SecondTableReference, markers, sources);
                    if (join is QualifiedJoin { SearchCondition: { } condition })
                    {
                        var on = PreviousSignificant(condition);
                        if (on is not null)
                        {
                            AddMarker(markers, on, CompletionClause.JoinCondition);
                        }
                    }

                    break;
                case JoinParenthesisTableReference parenthesis:
                    Collect(parenthesis.Join, markers, sources);
                    break;
                case NamedTableReference named:
                    var parts = new[] { named.SchemaObject.ServerIdentifier, named.SchemaObject.DatabaseIdentifier, named.SchemaObject.SchemaIdentifier, named.SchemaObject.BaseIdentifier }
                        .Where(i => i is not null)
                        .Select(i => i!.Value.ToLowerInvariant());
                    var exposed = (named.Alias ?? named.SchemaObject.BaseIdentifier).Value.ToLowerInvariant();
                    sources.Add(new OracleSource(exposed, "name:" + string.Join('.', parts), named.StartOffset, End(named)));
                    foreach (var hint in named.TableHints)
                    {
                        Exclude(hint);
                    }

                    if (named.TableHints.Count > 0)
                    {
                        var with = PreviousSignificant(named.TableHints[0]);
                        oracle._excluded.Add((with?.Offset ?? named.TableHints[0].StartOffset, End(named.TableHints[^1]) + 1));
                    }

                    Exclude(named.TableSampleClause);
                    Exclude(named.TemporalClause);
                    break;
                case VariableTableReference variable:
                    var name = variable.Variable.Name.ToLowerInvariant();
                    sources.Add(new OracleSource(variable.Alias?.Value.ToLowerInvariant() ?? name, "var:" + name, variable.StartOffset, End(variable)));
                    break;
                case QueryDerivedTable derived:
                    if (derived.Alias is not null)
                    {
                        sources.Add(new OracleSource(derived.Alias.Value.ToLowerInvariant(), "derived", derived.StartOffset, End(derived)));
                    }

                    if (derived.Columns.Count > 0)
                    {
                        oracle._excluded.Add((derived.Columns[0].StartOffset - 1, End(derived.Columns[^1]) + 1));
                    }

                    break;
                case TableReferenceWithAlias other:
                    if (other.Alias is not null)
                    {
                        sources.Add(new OracleSource(other.Alias.Value.ToLowerInvariant(), "derived", other.StartOffset, End(other)));
                    }

                    Exclude(other);
                    break;
                default:
                    Exclude(reference);
                    break;
            }
        }

        private List<OracleSource> TargetSources(TableReference? target, List<(int, CompletionClause)> markers)
        {
            var sources = new List<OracleSource>();
            if (target is null)
            {
                return sources;
            }

            markers.Add((target.StartOffset, CompletionClause.TableReference));
            Collect(target, markers, sources);
            return sources;
        }

        private void ExcludeOutput(TSqlFragment? output, TSqlFragment? outputInto)
        {
            Exclude(output);
            Exclude(outputInto);
        }

        /// <summary>ScriptDom's FOR XML/JSON fragment starts after <c>FOR XML</c>; exclude from the FOR keyword.</summary>
        private void ExcludeForClause(ForClause? clause)
        {
            if (clause is null)
            {
                return;
            }

            var start = clause.StartOffset;
            var tokens = clause.ScriptTokenStream;
            for (var i = clause.FirstTokenIndex - 1; i >= 0 && clause.FirstTokenIndex - i <= 6; i--)
            {
                if (tokens[i].TokenType == TSqlTokenType.For)
                {
                    start = tokens[i].Offset;
                    break;
                }
            }

            oracle._excluded.Add((start, End(clause)));
            Boundary(start);
        }

        private void Exclude(TSqlFragment? fragment)
        {
            if (fragment is null)
            {
                return;
            }

            oracle._excluded.Add((fragment.StartOffset, End(fragment)));
            Boundary(fragment.StartOffset);
        }

        private void Boundary(int offset) => _boundaries?.Add(offset);

        // ── tokens ──

        private static int End(TSqlFragment fragment) => fragment.StartOffset + fragment.FragmentLength;

        /// <summary>Fragment end extended over same-line trailing whitespace and comments, matching our caret extent.</summary>
        private static int CaretEnd(TSqlFragment fragment)
        {
            var end = End(fragment);
            var tokens = fragment.ScriptTokenStream;
            for (var i = fragment.LastTokenIndex + 1; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.TokenType == TSqlTokenType.WhiteSpace)
                {
                    var lineBreak = token.Text.IndexOfAny(['\r', '\n']);
                    if (lineBreak >= 0)
                    {
                        return end + lineBreak;
                    }

                    end += token.Text.Length;
                }
                else if (token.TokenType is TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                {
                    end += token.Text.Length;
                }
                else
                {
                    break;
                }
            }

            return end;
        }

        private static bool IsTrivia(TSqlParserToken token) =>
            token.TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment;

        private static TSqlParserToken? PreviousSignificant(TSqlFragment fragment)
        {
            var tokens = fragment.ScriptTokenStream;
            for (var i = fragment.FirstTokenIndex - 1; i >= 0; i--)
            {
                if (!IsTrivia(tokens[i]))
                {
                    return tokens[i];
                }
            }

            return null;
        }

        private static TSqlParserToken? NextSignificant(TSqlFragment fragment) =>
            NextSignificantToken(fragment.ScriptTokenStream, fragment.LastTokenIndex);

        private static TSqlParserToken? NextSignificantToken(IList<TSqlParserToken> tokens, int index)
        {
            for (var i = index + 1; i < tokens.Count; i++)
            {
                if (!IsTrivia(tokens[i]) && tokens[i].TokenType != TSqlTokenType.EndOfFile)
                {
                    return tokens[i];
                }
            }

            return null;
        }
    }
}
