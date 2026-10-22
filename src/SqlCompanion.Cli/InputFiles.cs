using Microsoft.Extensions.FileSystemGlobbing;

namespace SqlCompanion.Cli;

/// <summary>Result of expanding command-line path arguments.</summary>
/// <param name="Files">Distinct full paths, in ordinal order.</param>
/// <param name="Unmatched">Arguments that matched no file.</param>
public sealed record InputFileSet(IReadOnlyList<string> Files, IReadOnlyList<string> Unmatched);

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
        var unmatched = new List<string>();

        foreach (var argument in arguments)
        {
            var matches = ExpandOne(argument, currentDirectory);
            if (matches.Count == 0)
            {
                unmatched.Add(argument);
            }

            files.UnionWith(matches);
        }

        return new InputFileSet([.. files], unmatched);
    }

    private static List<string> ExpandOne(string argument, string currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            return [];
        }

        var fullPath = Path.GetFullPath(argument, currentDirectory);
        if (File.Exists(fullPath))
        {
            return [fullPath];
        }

        if (Directory.Exists(fullPath))
        {
            return Glob(fullPath, "**/*.sql");
        }

        if (argument.IndexOfAny(['*', '?']) < 0)
        {
            return [];
        }

        // Split at the first segment containing a wildcard: everything before it is the base directory.
        var segments = argument.Replace('\\', '/').Split('/');
        var firstWildcard = Array.FindIndex(segments, s => s.IndexOfAny(['*', '?']) >= 0);
        var baseDirectory = firstWildcard == 0
            ? currentDirectory
            : Path.GetFullPath(string.Join('/', segments[..firstWildcard]) + "/", currentDirectory);

        return Directory.Exists(baseDirectory)
            ? Glob(baseDirectory, string.Join('/', segments[firstWildcard..]))
            : [];
    }

    private static List<string> Glob(string baseDirectory, string pattern)
    {
        var matcher = new Matcher(OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        matcher.AddInclude(pattern);
        return [.. matcher.GetResultsInFullPath(baseDirectory).Select(Path.GetFullPath)];
    }
}
