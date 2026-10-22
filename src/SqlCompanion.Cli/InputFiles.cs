using Microsoft.Extensions.FileSystemGlobbing;

namespace SqlCompanion.Cli;

/// <summary>Result of expanding command-line path arguments.</summary>
/// <param name="Files">Distinct full paths, in ordinal order.</param>
/// <param name="Unmatched">Arguments that matched no file, with the reason.</param>
public sealed record InputFileSet(IReadOnlyList<string> Files, IReadOnlyList<UnmatchedInput> Unmatched);

/// <param name="Argument">The argument as given.</param>
/// <param name="Reason">Why it matched nothing, naming the first missing folder where there is one.</param>
public sealed record UnmatchedInput(string Argument, string Reason);

/// <summary>
/// Expands path arguments into SQL files. The CLI does this itself because Windows shells
/// (cmd, PowerShell) pass wildcards through literally, unlike POSIX shells.
/// </summary>
/// <remarks>
/// Each argument may be a file, a directory (all <c>*.sql</c> files beneath it, recursively), or a glob
/// pattern using <c>*</c>, <c>?</c> and <c>**</c> (e.g. <c>D:\scripts\*.sql</c>, <c>src/**/*.sql</c>).
/// Matching is case-insensitive on Windows.
/// </remarks>
public static class InputFiles
{
    public static InputFileSet Expand(IEnumerable<string> arguments, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var unmatched = new List<UnmatchedInput>();

        foreach (var argument in arguments)
        {
            var (matches, reason) = ExpandOne(argument, currentDirectory);
            if (matches.Count == 0)
            {
                unmatched.Add(new UnmatchedInput(argument, reason ?? "no files match"));
            }

            files.UnionWith(matches);
        }

        return new InputFileSet([.. files], unmatched);
    }

    private static (List<string> Files, string? Reason) ExpandOne(string argument, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return ([], "empty path");
        }

        var fullPath = Path.GetFullPath(argument, currentDirectory);
        if (File.Exists(fullPath))
        {
            return ([fullPath], null);
        }

        if (Directory.Exists(fullPath))
        {
            var files = Glob(fullPath, "**/*.sql");
            return (files, files.Count == 0 ? $"folder '{fullPath}' contains no .sql files" : null);
        }

        if (argument.IndexOfAny(['*', '?']) < 0)
        {
            var parent = Path.GetDirectoryName(fullPath);
            return ([], parent is not null && !Directory.Exists(parent) ? MissingFolder(parent) : $"file '{fullPath}' does not exist");
        }

        // Split at the first segment containing a wildcard: everything before it is the base directory.
        var segments = argument.Replace('\\', '/').Split('/');
        var firstWildcard = Array.FindIndex(segments, s => s.IndexOfAny(['*', '?']) >= 0);
        var baseDirectory = firstWildcard == 0
            ? currentDirectory
            : Path.GetFullPath(string.Join('/', segments[..firstWildcard]) + "/", currentDirectory);

        if (!Directory.Exists(baseDirectory))
        {
            return ([], MissingFolder(baseDirectory));
        }

        var pattern = string.Join('/', segments[firstWildcard..]);
        var matches = Glob(baseDirectory, pattern);
        return (matches, matches.Count == 0 ? $"no files match '{pattern}' in folder '{Path.TrimEndingDirectorySeparator(baseDirectory)}'" : null);
    }

    /// <summary>Names the shallowest missing folder, which is where a typo in the path usually is.</summary>
    private static string MissingFolder(string directory)
    {
        var missing = Path.TrimEndingDirectorySeparator(directory);
        while (Path.GetDirectoryName(missing) is { } parent && !Directory.Exists(parent))
        {
            missing = parent;
        }

        return $"folder '{missing}' does not exist";
    }

    private static List<string> Glob(string baseDirectory, string pattern)
    {
        var matcher = new Matcher(OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        matcher.AddInclude(pattern);
        return [.. matcher.GetResultsInFullPath(baseDirectory).Select(Path.GetFullPath)];
    }
}
