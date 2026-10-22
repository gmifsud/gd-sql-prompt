using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlCompanion.Engine.Binding;
using SqlCompanion.Engine.Syntax;
using SqlCompanion.Spikes.S2;

// Spike S2 — completion-context recovery of the tolerant parser, measured against ScriptDom.
// Usage: dotnet run -c Release --project spikes/S2.ParserRecovery -- <corpus-dir> [out-dir] [samples-per-file]

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: S2.ParserRecovery <corpus-dir> [out-dir] [samples-per-file]");
    return 2;
}

var corpusDir = Path.GetFullPath(args[0]);
var outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/s2");
var samplesPerFile = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 40;
const int MaxSampledFileBytes = 256 * 1024;
const int MaxRecordedMismatches = 2_000;
Directory.CreateDirectory(outDir);

var files = Directory.EnumerateFiles(corpusDir, "*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
var tally = new Tally();
var mismatches = new List<object>();
var recordedPerCategory = new Counter();
var ourParseTime = TimeSpan.Zero;
var oracleParseTime = TimeSpan.Zero;
long totalChars = 0;

foreach (var file in files)
{
    var relative = Path.GetRelativePath(corpusDir, file).Replace('\\', '/');
    var text = File.ReadAllText(file);
    totalChars += text.Length;
    tally.Files++;

    // Robustness: never throw, always lossless.
    SyntaxTree tree;
    var stopwatch = Stopwatch.StartNew();
    try
    {
        tree = SyntaxTree.Parse(text);
    }
    catch (Exception exception)
    {
        tally.Crashes++;
        Console.Error.WriteLine($"CRASH {relative}: {exception.GetType().Name} {exception.Message}");
        continue;
    }

    ourParseTime += stopwatch.Elapsed;
    if (tree.Root.ToFullString() != text)
    {
        tally.LossyTrees++;
    }

    stopwatch.Restart();
    var oracle = Oracle.TryBuild(text, out var oracleErrors);
    oracleParseTime += stopwatch.Elapsed;
    if (oracle is null)
    {
        tally.OracleRejected++;
        continue;
    }

    tally.OracleAccepted++;
    CompareStatementStarts(tree, oracle, tally, relative, text);

    if (text.Length * 2 > MaxSampledFileBytes)
    {
        tally.SkippedLarge++;
        continue;
    }

    var random = new Random(StableHash(relative));
    foreach (var candidate in SampleCandidates(text, oracle, random, samplesPerFile))
    {
        foreach (var perturbation in Enum.GetValues<Perturbation>())
        {
            var outcome = Evaluate(text, candidate, perturbation, oracle, random);
            if (outcome is null)
            {
                continue;
            }

            tally.Record(perturbation, outcome);
            var category = $"{perturbation}/{outcome.ExpectedClause}/{outcome.ActualClause}/{outcome.ClauseOk && !outcome.SourcesOk}";
            if (!outcome.Passed && mismatches.Count < MaxRecordedMismatches && recordedPerCategory.Increment(category) <= 40)
            {
                mismatches.Add(new
                {
                    file = relative,
                    perturbation = perturbation.ToString(),
                    position = outcome.Position,
                    expectedClause = outcome.ExpectedClause.ToString(),
                    actualClause = outcome.ActualClause.ToString(),
                    expectedSources = outcome.ExpectedSources,
                    actualSources = outcome.ActualSources,
                    before = Snippet(outcome.Text, Math.Max(0, outcome.Position - 80), outcome.Position),
                    after = Snippet(outcome.Text, outcome.Position, Math.Min(outcome.Text.Length, outcome.Position + 40)),
                });
            }
        }
    }
}

var report = tally.Render(ourParseTime, oracleParseTime, totalChars);
File.WriteAllText(Path.Combine(outDir, "report.md"), report);
File.WriteAllLines(Path.Combine(outDir, "mismatches.jsonl"), mismatches.Select(m => JsonSerializer.Serialize(m)));
File.WriteAllLines(Path.Combine(outDir, "statement-boundaries.log"), tally.BoundaryLog);
Console.WriteLine(report);
return tally.Crashes == 0 && tally.LossyTrees == 0 ? 0 : 1;

static void CompareStatementStarts(SyntaxTree tree, Oracle oracle, Tally tally, string file, string text)
{
    var containers = new HashSet<SyntaxKind>
    {
        SyntaxKind.Batch, SyntaxKind.BlockStatement, SyntaxKind.TryCatchStatement, SyntaxKind.CreateModuleStatement,
        SyntaxKind.IfStatement, SyntaxKind.ElseClause, SyntaxKind.WhileStatement,
    };

    var ours = new Dictionary<int, string>();
    // ScriptDom models view and inline-function bodies as a query, not a statement list: compare like with like.
    static bool IsSingleQueryModule(SyntaxNode module)
    {
        var header = module.ChildNodes().FirstOrDefault(n => n.Kind == SyntaxKind.ModuleHeader);
        var words = header?.DescendantTokens().Select(t => t.Text.ToUpperInvariant()).ToList() ?? [];
        var returns = words.IndexOf("RETURNS");
        return words.Contains("VIEW") || returns >= 0 && returns + 1 < words.Count && words[returns + 1] == "TABLE";
    }

    foreach (var container in tree.Root.DescendantNodes().Where(n => containers.Contains(n.Kind)
        && !(n.Kind == SyntaxKind.CreateModuleStatement && IsSingleQueryModule(n))))
    {
        foreach (var child in container.ChildNodes())
        {
            if (child.Kind is SyntaxKind.EmptyStatement or SyntaxKind.ErrorNode or SyntaxKind.ModuleHeader or SyntaxKind.ElseClause
                || child.Kind is SyntaxKind.Expression or SyntaxKind.ParenthesizedExpression or SyntaxKind.Subquery)
            {
                continue;
            }

            ours[child.Span.Start] = child.FirstToken.Text.ToUpperInvariant();
        }
    }

    var expected = oracle.StatementStarts.ToHashSet();
    tally.OracleStatements += expected.Count;
    tally.OurStatements += ours.Count;
    tally.MatchedStatements += expected.Count(ours.ContainsKey);
    string Line(int offset) => text[offset..Math.Min(text.Length, offset + 60)].ReplaceLineEndings(" ");
    foreach (var missed in expected.Where(s => !ours.ContainsKey(s)))
    {
        var type = oracle.StatementTypes[missed];
        if (tally.MissedStatementTypes.Increment(type) <= 15)
        {
            tally.BoundaryLog.Add($"MISSED {type} {file}@{missed}: {Line(missed)}");
        }
    }

    foreach (var extra in ours.Where(o => !expected.Contains(o.Key)))
    {
        if (tally.ExtraStatementHeads.Increment(extra.Value) <= 15)
        {
            tally.BoundaryLog.Add($"EXTRA {extra.Value} {file}@{extra.Key}: {Line(extra.Key)}");
        }
    }
}

static IEnumerable<Candidate> SampleCandidates(string text, Oracle oracle, Random random, int count)
{
    var candidates = new List<Candidate>();
    foreach (var token in Lexer.Lex(text).Tokens)
    {
        if (token.Kind is SyntaxKind.IdentifierToken or SyntaxKind.QuotedIdentifierToken or SyntaxKind.VariableToken or SyntaxKind.DotToken
            && oracle.Classify(token.Span.End) is not null)
        {
            candidates.Add(new Candidate(token.Span.End, token));
        }

        if (token.Kind is not (SyntaxKind.KeywordToken or SyntaxKind.EndOfFileToken) && oracle.Classify(token.Position) is not null)
        {
            candidates.Add(new Candidate(token.Position, null));
        }
    }

    return candidates.OrderBy(_ => random.Next()).Take(count).ToList();
}

static Outcome? Evaluate(string text, Candidate candidate, Perturbation perturbation, Oracle oracle, Random random)
{
    var position = candidate.Position;
    int cutEnd;
    string insert;

    switch (perturbation)
    {
        case Perturbation.Intact:
            cutEnd = position;
            insert = string.Empty;
            break;
        case Perturbation.PartialWord:
            // Caret inside an unquoted word whose remainder has not been typed yet.
            if (candidate.Token is not { Kind: SyntaxKind.IdentifierToken or SyntaxKind.VariableToken, Text.Length: >= 2 } word)
            {
                return null;
            }

            position = word.Position + random.Next(1, word.Text.Length);
            cutEnd = word.Span.End;
            insert = string.Empty;
            break;
        case Perturbation.ClauseTail:
        case Perturbation.StatementTail:
            var probe = oracle.Classify(position);
            if (probe is null)
            {
                return null;
            }

            cutEnd = perturbation == Perturbation.ClauseTail ? probe.RegionEnd : probe.StatementEnd;
            if (cutEnd <= position)
            {
                return null;
            }

            insert = "\n";
            break;
        default:
            throw new UnreachableException();
    }

    var expected = oracle.Classify(position);
    if (expected is null)
    {
        return null;
    }

    var edited = string.Concat(text.AsSpan(0, position), insert, text.AsSpan(cutEnd));
    var actual = CompletionContextResolver.Resolve(SyntaxTree.Parse(edited), position);

    var compareSources = expected.Clause is CompletionClause.SelectList or CompletionClause.JoinCondition or CompletionClause.Where
        or CompletionClause.GroupBy or CompletionClause.Having or CompletionClause.OrderBy or CompletionClause.SetList
        or CompletionClause.InsertColumns;

    // Filter by what survives the edit first, then apply the target/FROM de-duplication, as the resolver
    // sees only the edited text (UPDATE rw … FROM t AS rw with FROM deleted leaves rw as a table name).
    bool Survives(OracleSource s) => s.End < position || s.Start > position && s.Start >= cutEnd;
    var targets = expected.TargetSources.Where(Survives).ToList();
    var fromSources = expected.Sources.Where(s => !expected.TargetSources.Contains(s)).Where(Survives).ToList();
    var fromExposed = fromSources.Select(s => s.ExposedName).ToHashSet();
    var expectedSources = expected.Clause == CompletionClause.InsertColumns
        ? targets
        : [.. targets.Where(t => !fromExposed.Contains(t.ExposedName)), .. fromSources];
    var expectedSet = expectedSources.Select(s => $"{s.ExposedName}={s.Descriptor}").ToHashSet();
    var actualSet = actual.Sources
        .Where(s => s.Span.End < position || s.Span.Start > position)
        .Select(s => $"{s.ExposedName}={s.Descriptor}")
        .ToHashSet();

    var clauseOk = expected.Clause == actual.Clause;
    var sourcesOk = !compareSources || expectedSet.SetEquals(actualSet);
    return new Outcome(edited, position, expected.Clause, actual.Clause, clauseOk, compareSources, sourcesOk,
        [.. expectedSet.Order()], [.. actualSet.Order()]);
}

static string Snippet(string text, int start, int end) => text[start..end].Replace("\r", "", StringComparison.Ordinal);

/// <summary>FNV-1a: deterministic across processes, unlike <see cref="string.GetHashCode()"/>.</summary>
static int StableHash(string value)
{
    var hash = 2166136261u;
    foreach (var c in value)
    {
        hash = (hash ^ c) * 16777619u;
    }

    return (int)(hash & 0x7FFFFFFF);
}

internal enum Perturbation
{
    /// <summary>Complete script, caret at a token boundary (baseline).</summary>
    Intact,

    /// <summary>Caret inside a word whose remainder is not yet typed.</summary>
    PartialWord,

    /// <summary>Remainder of the current clause deleted (e.g. typing a new WHERE predicate).</summary>
    ClauseTail,

    /// <summary>Remainder of the current statement deleted (writing a statement top-down).</summary>
    StatementTail,
}

internal sealed record Candidate(int Position, SyntaxToken? Token);

internal sealed record Outcome(
    string Text,
    int Position,
    CompletionClause ExpectedClause,
    CompletionClause ActualClause,
    bool ClauseOk,
    bool SourcesCompared,
    bool SourcesOk,
    string[] ExpectedSources,
    string[] ActualSources)
{
    public bool Passed => ClauseOk && SourcesOk;
}

internal sealed class Counter : Dictionary<string, int>
{
    public int Increment(string key) => this[key] = TryGetValue(key, out var value) ? value + 1 : 1;
}

internal sealed class Tally
{
    private readonly Dictionary<Perturbation, (int Samples, int Passed, int ClauseOk, int SourcesCompared, int SourcesOk)> _byPerturbation = [];
    private readonly Dictionary<(Perturbation, CompletionClause), (int Samples, int Passed)> _byClause = [];

    public int Files { get; set; }

    public int Crashes { get; set; }

    public int LossyTrees { get; set; }

    public int OracleAccepted { get; set; }

    public int OracleRejected { get; set; }

    public int SkippedLarge { get; set; }

    public int OracleStatements { get; set; }

    public int OurStatements { get; set; }

    public int MatchedStatements { get; set; }

    public Counter MissedStatementTypes { get; } = [];

    public Counter ExtraStatementHeads { get; } = [];

    public List<string> BoundaryLog { get; } = [];

    public void Record(Perturbation perturbation, Outcome outcome)
    {
        var p = _byPerturbation.GetValueOrDefault(perturbation);
        _byPerturbation[perturbation] = (
            p.Samples + 1,
            p.Passed + (outcome.Passed ? 1 : 0),
            p.ClauseOk + (outcome.ClauseOk ? 1 : 0),
            p.SourcesCompared + (outcome.SourcesCompared ? 1 : 0),
            p.SourcesOk + (outcome.SourcesCompared && outcome.SourcesOk ? 1 : 0));

        var key = (perturbation, outcome.ExpectedClause);
        var c = _byClause.GetValueOrDefault(key);
        _byClause[key] = (c.Samples + 1, c.Passed + (outcome.Passed ? 1 : 0));
    }

    public string Render(TimeSpan ours, TimeSpan oracle, long chars)
    {
        static string Pct(int part, int whole) => whole == 0 ? "—" : (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + " %";
        var sb = new StringBuilder();
        sb.AppendLine("## Corpus");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Files | {Files} |");
        sb.AppendLine($"| Parser crashes | {Crashes} |");
        sb.AppendLine($"| Lossy trees (round-trip failures) | {LossyTrees} |");
        sb.AppendLine($"| Accepted by ScriptDom (oracle) | {OracleAccepted} |");
        sb.AppendLine($"| Rejected by ScriptDom (excluded from context metrics) | {OracleRejected} |");
        sb.AppendLine($"| Too large to sample (> 256 KB) | {SkippedLarge} |");
        sb.AppendLine($"| Characters parsed | {chars:N0} |");
        sb.AppendLine($"| Our parse time (all files) | {ours.TotalMilliseconds:N0} ms |");
        sb.AppendLine($"| ScriptDom parse time (all files) | {oracle.TotalMilliseconds:N0} ms |");
        sb.AppendLine();
        sb.AppendLine("## Completion context");
        sb.AppendLine();
        sb.AppendLine("| Edit state | Samples | Context correct | Clause correct | Sources correct (where compared) |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (perturbation, v) in _byPerturbation.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"| {perturbation} | {v.Samples:N0} | {Pct(v.Passed, v.Samples)} | {Pct(v.ClauseOk, v.Samples)} | {Pct(v.SourcesOk, v.SourcesCompared)} ({v.SourcesCompared:N0}) |");
        }

        sb.AppendLine();
        sb.AppendLine("### By clause (context correct)");
        sb.AppendLine();
        var perturbations = _byPerturbation.Keys.Order().ToList();
        sb.AppendLine("| Clause | " + string.Join(" | ", perturbations) + " |");
        sb.AppendLine("|---|" + string.Concat(perturbations.Select(_ => "---:|")));
        foreach (var clause in _byClause.Keys.Select(k => k.Item2).Distinct().Order())
        {
            var cells = perturbations.Select(p => _byClause.TryGetValue((p, clause), out var v) ? $"{Pct(v.Passed, v.Samples)} ({v.Samples:N0})" : "—");
            sb.AppendLine($"| {clause} | " + string.Join(" | ", cells) + " |");
        }

        sb.AppendLine();
        sb.AppendLine("## Statement boundaries (complete scripts)");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Recall (ScriptDom statement starts we also found) | {Pct(MatchedStatements, OracleStatements)} ({MatchedStatements:N0} / {OracleStatements:N0}) |");
        sb.AppendLine($"| Precision (our statement starts ScriptDom agrees with) | {Pct(MatchedStatements, OurStatements)} ({MatchedStatements:N0} / {OurStatements:N0}) |");
        sb.AppendLine();
        sb.AppendLine("Top missed statement types: " + string.Join(", ", MissedStatementTypes.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} ({kv.Value})")));
        sb.AppendLine();
        sb.AppendLine("Top extra statement heads: " + string.Join(", ", ExtraStatementHeads.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} ({kv.Value})")));
        return sb.ToString();
    }
}
