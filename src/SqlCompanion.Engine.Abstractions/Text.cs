namespace SqlCompanion.Engine;

/// <summary>A half-open range <c>[Start, End)</c> of UTF-16 code units in a document.</summary>
public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    /// <summary>True when <paramref name="position"/> lies in <c>[Start, End)</c>.</summary>
    public bool Contains(int position) => position >= Start && position < End;

    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>A single replacement of <see cref="Span"/> with <see cref="NewText"/>.</summary>
public sealed record TextEdit(TextSpan Span, string NewText);

/// <summary>
/// An immutable view of a document at a specific version. Results computed from a snapshot
/// must be discarded if the document has since moved to a newer version.
/// </summary>
public sealed record DocumentSnapshot(string Uri, int Version, string Text);
