using CsCheck;

namespace SqlCompanion.Engine.Tests;

public sealed class LineMapTests
{
    [Theory]
    [InlineData("a\nb", 2, 1, 0)]
    [InlineData("a\r\nb", 3, 1, 0)]
    [InlineData("a\rb", 2, 1, 0)]
    [InlineData("a\r\nb", 2, 0, 2)]
    [InlineData("", 0, 0, 0)]
    public void GetPosition_HandlesAllLineBreakForms(string text, int offset, int line, int character) =>
        Assert.Equal(new LinePosition(line, character), new LineMap(text).GetPosition(offset));

    [Fact]
    public void GetOffset_IsInverseOfGetPosition() =>
        Gen.Char["ab\r\n"].Array[0, 60].Select(c => new string(c)).Sample(text =>
        {
            var map = new LineMap(text);
            for (var offset = 0; offset <= text.Length; offset++)
            {
                // Offset between '\r' and '\n' is not a valid position boundary in LSP; skip it.
                if (offset > 0 && offset < text.Length && text[offset - 1] == '\r' && text[offset] == '\n')
                {
                    continue;
                }

                Assert.Equal(offset, map.GetOffset(map.GetPosition(offset)));
            }
        }, iter: 5_000);

    [Fact]
    public void GetOffset_ClampsOutOfRangePositions()
    {
        var map = new LineMap("ab\ncd");
        Assert.Equal(3, map.GetOffset(new LinePosition(0, 99)));
        Assert.Equal(5, map.GetOffset(new LinePosition(99, 0)));
        Assert.Equal(0, map.GetOffset(new LinePosition(-1, 0)));
    }
}
