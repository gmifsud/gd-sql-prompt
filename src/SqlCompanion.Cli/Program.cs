using System.CommandLine;
using SqlCompanion.Cli;
using SqlCompanion.Engine;
using SqlCompanion.Engine.Analysis;
using SqlCompanion.Engine.Syntax;

// Exit codes: 0 = success, 1 = SQL errors reported, 2 = invalid input (no matching or unreadable files).
const int ExitInputError = 2;

var lexFile = new Argument<string>("file") { Description = "SQL file to tokenise." };
var lexCommand = new Command("lex", "Print the token stream with trivia (parser spike S2 diagnostics).") { lexFile };
lexCommand.SetAction(parseResult =>
{
    var path = Path.GetFullPath(parseResult.GetValue(lexFile)!);
    if (!TryRead(path, out var text))
    {
        return ExitInputError;
    }

    var lex = Lexer.Lex(text);
    var lines = new LineMap(text);

    foreach (var token in lex.Tokens)
    {
        foreach (var trivia in token.LeadingTrivia)
        {
            Write(lines, trivia.Position, $"  {trivia.Kind}", trivia.Text);
        }

        Write(lines, token.Position, token.Kind.ToString(), token.Text);

        foreach (var trivia in token.TrailingTrivia)
        {
            Write(lines, trivia.Position, $"  {trivia.Kind}", trivia.Text);
        }
    }

    return 0;
});

var analyzeFiles = new Argument<string[]>("paths")
{
    Description = "Files, directories (searched recursively for *.sql) or glob patterns such as D:\\scripts\\*.sql or src/**/*.sql.",
    Arity = ArgumentArity.OneOrMore,
};
var analyzeCommand = new Command(
    "analyze",
    "Report diagnostics in MSBuild canonical format. Exit code 1 if any error is found, 2 if an input matches no file.")
{
    analyzeFiles,
};
analyzeCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var inputs = InputFiles.Expand(parseResult.GetValue(analyzeFiles)!, Environment.CurrentDirectory);
    foreach (var input in inputs.Unmatched)
    {
        await Console.Error.WriteLineAsync($"sqlwc: '{input.Argument}': {input.Reason}.").ConfigureAwait(false);
    }

    if (inputs.Unmatched.Count > 0)
    {
        return ExitInputError;
    }

    var service = new SyntaxDiagnosticService();
    var hasErrors = false;

    foreach (var file in inputs.Files)
    {
        if (!TryRead(file, out var text))
        {
            return ExitInputError;
        }

        var snapshot = new DocumentSnapshot(new Uri(file).AbsoluteUri, 0, text);
        var diagnostics = await service.GetDiagnosticsAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var lines = new LineMap(text);

        foreach (var d in diagnostics)
        {
            var p = lines.GetPosition(d.Span.Start);
            var severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            Console.WriteLine($"{file}({p.Line + 1},{p.Character + 1}): {severity} {d.Id}: {d.Message}");
            hasErrors |= d.Severity == DiagnosticSeverity.Error;
        }
    }

    return hasErrors ? 1 : 0;
});

var root = new RootCommand("SQL Workbench Companion command-line tools.") { lexCommand, analyzeCommand };
return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static bool TryRead(string path, out string text)
{
    try
    {
        text = File.ReadAllText(path);
        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"sqlwc: cannot read '{path}': {exception.Message}");
        text = string.Empty;
        return false;
    }
}

static void Write(LineMap lines, int position, string kind, string text)
{
    var p = lines.GetPosition(position);
    var escaped = text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
    Console.WriteLine($"{p.Line + 1,5}:{p.Character + 1,-4} {kind,-28} \"{escaped}\"");
}
