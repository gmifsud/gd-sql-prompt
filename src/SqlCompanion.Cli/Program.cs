using System.CommandLine;
using SqlCompanion.Engine;
using SqlCompanion.Engine.Analysis;
using SqlCompanion.Engine.Syntax;

var lexFile = new Argument<FileInfo>("file") { Description = "SQL file to tokenise." };
var lexCommand = new Command("lex", "Print the token stream with trivia (parser spike S2 diagnostics).") { lexFile };
lexCommand.SetAction(parseResult =>
{
    var file = parseResult.GetValue(lexFile)!;
    var text = File.ReadAllText(file.FullName);
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

var analyzeFiles = new Argument<FileInfo[]>("files") { Description = "SQL files to analyse.", Arity = ArgumentArity.OneOrMore };
var analyzeCommand = new Command("analyze", "Report diagnostics in MSBuild canonical format. Exit code 1 if any error is found.") { analyzeFiles };
analyzeCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var service = new SyntaxDiagnosticService();
    var hasErrors = false;

    foreach (var file in parseResult.GetValue(analyzeFiles)!)
    {
        var text = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
        var snapshot = new DocumentSnapshot(new Uri(file.FullName).AbsoluteUri, 0, text);
        var diagnostics = await service.GetDiagnosticsAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var lines = new LineMap(text);

        foreach (var d in diagnostics)
        {
            var p = lines.GetPosition(d.Span.Start);
            var severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            Console.WriteLine($"{file.FullName}({p.Line + 1},{p.Character + 1}): {severity} {d.Id}: {d.Message}");
            hasErrors |= d.Severity == DiagnosticSeverity.Error;
        }
    }

    return hasErrors ? 1 : 0;
});

var root = new RootCommand("SQL Workbench Companion command-line tools.") { lexCommand, analyzeCommand };
return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static void Write(LineMap lines, int position, string kind, string text)
{
    var p = lines.GetPosition(position);
    var escaped = text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
    Console.WriteLine($"{p.Line + 1,5}:{p.Character + 1,-4} {kind,-28} \"{escaped}\"");
}
