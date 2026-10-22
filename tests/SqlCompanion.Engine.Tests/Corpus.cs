namespace SqlCompanion.Engine.Tests;

internal static class Corpus
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Corpus");

    /// <summary>Corpus files relative to <see cref="Root"/>, using '/' separators for stable test names.</summary>
    public static TheoryData<string> Files(string? folder = null)
    {
        var directory = folder is null ? Root : Path.Combine(Root, folder);
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.sql", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(Root, path).Replace('\\', '/'));
        }

        return data;
    }

    public static string Read(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));
}
