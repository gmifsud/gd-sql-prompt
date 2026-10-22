using System.Text;
using BenchmarkDotNet.Attributes;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Benchmarks;

/// <summary>Full tolerant parse (lex + CST) for scripts of increasing size; spec §13 reference size is 5,000 lines.</summary>
[MemoryDiagnoser]
public class ParserBenchmarks
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
    public int Parse() => SyntaxTree.Parse(_script).Root.Children.Length;
}
