using System.Collections.Frozen;

namespace SqlCompanion.Engine.Formatting;

public enum KeywordCasing
{
    Upper,
    Lower,
    Preserve,
}

public enum CommaPlacement
{
    /// <summary><c>a,</c> ⏎ <c>b</c></summary>
    Trailing,

    /// <summary><c>a</c> ⏎ <c>, b</c></summary>
    Leading,
}

public enum LineEndingPolicy
{
    /// <summary>Use the document's predominant line ending (CRLF when it has none).</summary>
    Preserve,
    CrLf,
    Lf,
}

/// <summary>
/// Formatting options (FMT-002 casing, FMT-003 whitespace, FMT-004 clause layout, FMT-006 commas; FMT-009 profiles).
/// </summary>
/// <param name="Name">Profile name, as accepted by <see cref="StyleProfile.Find"/>.</param>
/// <param name="Keywords">Casing of reserved keywords. Non-reserved words, functions and data types are not cased yet.</param>
/// <param name="IndentSize">Spaces per indentation level (ignored with <paramref name="UseTabs"/>).</param>
/// <param name="UseTabs">Indent with one tab per level.</param>
/// <param name="MaxLineWidth">Width a group must fit in to stay on one line.</param>
/// <param name="Commas">Comma placement when a list is broken across lines.</param>
/// <param name="CollapseShortQueries">Keep a query on one line when it fits (<c>SELECT a FROM t WHERE x = 1</c>); otherwise every clause starts a line.</param>
/// <param name="IndentJoins">Indent <c>JOIN</c> clauses one level under <c>FROM</c>.</param>
/// <param name="LineEndings">Line endings written by the formatter; literals and comments keep their own.</param>
/// <param name="MaxBlankLines">Blank lines kept between statements and around comments (0 or 1).</param>
public sealed record StyleProfile(
    string Name,
    KeywordCasing Keywords = KeywordCasing.Upper,
    int IndentSize = 4,
    bool UseTabs = false,
    int MaxLineWidth = 120,
    CommaPlacement Commas = CommaPlacement.Trailing,
    bool CollapseShortQueries = false,
    bool IndentJoins = true,
    LineEndingPolicy LineEndings = LineEndingPolicy.Preserve,
    int MaxBlankLines = 1)
{
    /// <summary>Upper-case keywords, four-space indent, every clause on its own line, trailing commas.</summary>
    public static StyleProfile Default { get; } = new("default");

    /// <summary>Short queries stay on one line; long ones break as in <see cref="Default"/>.</summary>
    public static StyleProfile Compact { get; } = new("compact", CollapseShortQueries: true);

    /// <summary><see cref="Default"/> with leading commas.</summary>
    public static StyleProfile LeadingCommas { get; } = new("leading-commas", Commas: CommaPlacement.Leading);

    /// <summary><see cref="Default"/> with lower-case keywords.</summary>
    public static StyleProfile Lowercase { get; } = new("lowercase", Keywords: KeywordCasing.Lower);

    public static FrozenDictionary<string, StyleProfile> BuiltIn { get; } =
        new[] { Default, Compact, LeadingCommas, Lowercase }.ToFrozenDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public static StyleProfile? Find(string name) => BuiltIn.GetValueOrDefault(name);

    internal string IndentUnit => UseTabs ? "\t" : new string(' ', Math.Max(0, IndentSize));
}
