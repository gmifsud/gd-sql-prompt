using System.CommandLine;
using SqlCompanion.Cli;
using SqlCompanion.Engine;
using SqlCompanion.Engine.Analysis;
using SqlCompanion.Engine.Formatting;
using SqlCompanion.Engine.Syntax;

// Exit codes: 0 = success, 1 = SQL errors reported / files need formatting (--check),
// 2 = invalid input (no matching or unreadable files), 3 = a file was left unformatted by the formatter's safety check.
const int ExitInputError = 2;
const int ExitFormatterSkipped = 3;

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

var formatFiles = new Argument<string[]>("paths")
{
    Description = "Files, directories (searched recursively for *.sql) or glob patterns.",
    Arity = ArgumentArity.OneOrMore,
};
var formatCheck = new Option<bool>("--check") { Description = "Change nothing; list files that would be reformatted and exit 1 if there are any." };
var formatWrite = new Option<bool>("--write") { Description = "Rewrite files in place (encoding and byte-order mark preserved)." };
var formatStyle = new Option<string>("--style")
{
    Description = $"Built-in style profile: {string.Join(", ", StyleProfile.BuiltIn.Keys.Order(StringComparer.Ordinal))}.",
    DefaultValueFactory = _ => StyleProfile.Default.Name,
};
var formatCommand = new Command(
    "format",
    "Format SQL files. Without --check or --write, prints the formatted text of each file to standard output.")
{
    formatFiles,
    formatCheck,
    formatWrite,
    formatStyle,
};
formatCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var profile = StyleProfile.Find(parseResult.GetValue(formatStyle)!);
    if (profile is null)
    {
        await Console.Error.WriteLineAsync($"sqlwc: unknown style '{parseResult.GetValue(formatStyle)}'.").ConfigureAwait(false);
        return ExitInputError;
    }

    var inputs = InputFiles.Expand(parseResult.GetValue(formatFiles)!, Environment.CurrentDirectory);
    foreach (var input in inputs.Unmatched)
    {
        await Console.Error.WriteLineAsync($"sqlwc: '{input.Argument}': {input.Reason}.").ConfigureAwait(false);
    }

    if (inputs.Unmatched.Count > 0)
    {
        return ExitInputError;
    }

    var check = parseResult.GetValue(formatCheck);
    var write = parseResult.GetValue(formatWrite);
    var unformatted = 0;
    var skipped = 0;
    foreach (var file in inputs.Files)
    {
        if (!SourceFile.TryRead(file, out var source))
        {
            return ExitInputError;
        }

        var result = SqlFormatter.Format(source.Text, profile);
        if (result.SkippedReason is not null)
        {
            skipped++;
            await Console.Error.WriteLineAsync($"{file}: skipped, left unchanged (formatter safety check: {result.SkippedReason}).").ConfigureAwait(false);
            continue;
        }

        if (check || write)
        {
            if (result.Changed)
            {
                unformatted++;
                Console.WriteLine(check ? $"{file}: would reformat" : $"{file}: reformatted");
                if (write && !source.TryWrite(result.Text))
                {
                    return ExitInputError;
                }
            }
        }
        else
        {
            Console.Write(result.Text);
        }
    }

    if (check || write)
    {
        Console.WriteLine($"{inputs.Files.Count} file(s): {unformatted} {(check ? "would be reformatted" : "reformatted")}, {skipped} skipped.");
    }

    return skipped > 0 ? ExitFormatterSkipped : check && unformatted > 0 ? 1 : 0;
});

var root = new RootCommand("SQL Workbench Companion command-line tools.") { lexCommand, analyzeCommand, formatCommand };
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
