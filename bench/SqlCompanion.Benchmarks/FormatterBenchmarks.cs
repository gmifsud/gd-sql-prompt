using System.Text;
using BenchmarkDotNet.Attributes;
using SqlCompanion.Engine.Formatting;

namespace SqlCompanion.Benchmarks;

/// <summary>
/// Full format including the safety check (re-parse and token, comment and structure comparison).
/// Roadmap Phase 1 gate: a 5,000-line script formats in under 2 s.
/// </summary>
[MemoryDiagnoser]
public class FormatterBenchmarks
{
    private string _script = string.Empty;

    [Params(500, 5_000)]
    public int Lines { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var unit = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Corpus", "procedure.sql"));
        var unitLines = unit.Count(c => c == '\n');
        var builder = new StringBuilder();
        for (var written = 0; written < Lines; written += unitLines)
        {
            builder.Append(unit);
        }

        _script = builder.ToString();
    }

    [Benchmark]
    public int Format() => SqlFormatter.Format(_script, StyleProfile.Default).Text.Length;
}
