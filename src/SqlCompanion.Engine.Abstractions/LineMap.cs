namespace SqlCompanion.Engine;

/// <summary>Zero-based line and UTF-16 character offset, matching LSP <c>Position</c> semantics.</summary>
public readonly record struct LinePosition(int Line, int Character);

/// <summary>
/// Maps between absolute offsets and line/character positions.
/// Line breaks are <c>\r\n</c>, <c>\n</c> or <c>\r</c>.
/// </summary>
public sealed class LineMap
{
    private readonly int[] _lineStarts;
    private readonly int _length;

    public LineMap(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _length = text.Length;

        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }

        _lineStarts = [.. starts];
    }

    public int LineCount => _lineStarts.Length;

    public LinePosition GetPosition(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, _length);

        var index = Array.BinarySearch(_lineStarts, offset);
        var line = index >= 0 ? index : ~index - 1;
        return new LinePosition(line, offset - _lineStarts[line]);
    }

    /// <summary>Converts a position to an offset, clamping out-of-range values to the document bounds.</summary>
    public int GetOffset(LinePosition position)
    {
        if (position.Line < 0)
        {
            return 0;
        }

        if (position.Line >= _lineStarts.Length)
        {
            return _length;
        }

        var lineStart = _lineStarts[position.Line];
        var lineEnd = position.Line + 1 < _lineStarts.Length ? _lineStarts[position.Line + 1] : _length;
        return Math.Clamp(lineStart + position.Character, lineStart, lineEnd);
    }
}
