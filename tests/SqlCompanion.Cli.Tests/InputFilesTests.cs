namespace SqlCompanion.Cli.Tests;

public sealed class InputFilesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sqlwc-inputs-").FullName;

    public InputFilesTests()
    {
        Touch("a.sql");
        Touch("b.SQL");
        Touch("notes.txt");
        Touch(Path.Combine("sub", "c.sql"));
    }

    [Fact]
    public void ExplicitFile_IsReturned() =>
        AssertFiles(Expand("a.sql"), "a.sql");

    [Fact]
    public void Directory_IsSearchedRecursivelyForSqlFiles() =>
        AssertFiles(Expand("."), "a.sql", Path.Combine("sub", "c.sql"));

    [Fact]
    public void Wildcard_IsExpandedByTheCliNotTheShell() =>
        AssertFiles(Expand("*.sql"), "a.sql");

    [Fact]
    public void AbsoluteWildcardWithNativeSeparators_IsExpanded() =>
        AssertFiles(Expand(Path.Combine(_root, "*.sql")), "a.sql");

    [Fact]
    public void BackslashSeparators_AreAccepted() =>
        AssertFiles(Expand("sub\\*.sql"), Path.Combine("sub", "c.sql"));

    [Fact]
    public void DoubleStar_RecursesIntoSubdirectories() =>
        AssertFiles(Expand("**/*.sql"), "a.sql", Path.Combine("sub", "c.sql"));

    [Fact]
    public void OverlappingArguments_AreDeduplicated() =>
        AssertFiles(Expand("a.sql", "*.sql"), "a.sql");

    [Theory]
    [InlineData("missing.sql")]
    [InlineData("path/to/*.sql")]
    [InlineData("*.nothing")]
    public void ArgumentsMatchingNothing_AreReported(string argument)
    {
        var result = Expand(argument);
        Assert.Empty(result.Files);
        Assert.Equal([argument], result.Unmatched.Select(u => u.Argument));
    }

    [Theory]
    [InlineData("typo/sub/**/*.sql")]
    [InlineData("typo/sub/a.sql")]
    public void MissingFolder_IsNamedAtTheFirstMissingSegment(string argument)
    {
        var reason = Assert.Single(Expand(argument).Unmatched).Reason;
        Assert.Equal($"folder '{Path.Combine(_root, "typo")}' does not exist", reason);
    }

    [Fact]
    public void PatternInExistingFolder_ReportsThePatternAndFolder()
    {
        var reason = Assert.Single(Expand("sub/*.nothing").Unmatched).Reason;
        Assert.Equal($"no files match '*.nothing' in folder '{Path.Combine(_root, "sub")}'", reason);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private InputFileSet Expand(params string[] arguments) => InputFiles.Expand(arguments, _root);

    private void AssertFiles(InputFileSet result, params string[] expected)
    {
        Assert.Empty(result.Unmatched);
        var expectedFull = expected.Select(e => Path.Combine(_root, e)).ToList();

        // b.SQL matches *.sql only where the file system is case-insensitive (Windows).
        if (OperatingSystem.IsWindows() && result.Files.Any(f => f.EndsWith("b.SQL", StringComparison.Ordinal)))
        {
            expectedFull.Add(Path.Combine(_root, "b.SQL"));
        }

        Assert.Equal(expectedFull.Order(StringComparer.Ordinal), result.Files);
    }

    private void Touch(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "SELECT 1;");
    }
}
