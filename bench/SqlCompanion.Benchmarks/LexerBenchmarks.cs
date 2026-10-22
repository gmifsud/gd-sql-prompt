using System.Text;
using BenchmarkDotNet.Attributes;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Benchmarks;

/// <summary>
/// Lexing cost for scripts of increasing size. The 5,000-line case is the spec §13 formatting
/// reference size; lexing is the first stage of that budget (&lt; 2 s end to end).
/// </summary>
[MemoryDiagnoser]
public class LexerBenchmarks
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
    public int Lex() => Lexer.Lex(_script).Tokens.Length;
}
