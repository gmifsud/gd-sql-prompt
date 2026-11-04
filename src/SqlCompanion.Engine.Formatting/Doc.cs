using System.Collections.Immutable;
using System.Text;

namespace SqlCompanion.Engine.Formatting;

/// <summary>
/// Layout document (Wadler, <i>A Prettier Printer</i>; Leijen's <c>group</c> / <c>nest</c> variant). A
/// <see cref="Group"/> is printed flat when it fits in the remaining width, otherwise its line breaks are taken.
/// </summary>
public abstract class Doc
{
    public static readonly Doc Empty = new ConcatDoc([]);

    /// <summary>A space, collapsed with adjacent spaces and dropped at the start of a line.</summary>
    public static readonly Doc Space = new SpaceDoc();

    /// <summary>A space when the enclosing group is flat, a line break otherwise.</summary>
    public static readonly Doc Line = new LineDoc(LineKind.Line);

    /// <summary>Nothing when the enclosing group is flat, a line break otherwise.</summary>
    public static readonly Doc SoftLine = new LineDoc(LineKind.Soft);

    /// <summary>Always a line break; forces every enclosing group to break.</summary>
    public static readonly Doc HardLine = new LineDoc(LineKind.Hard);

    /// <summary>A line break followed by one empty line (consecutive blank lines collapse to one).</summary>
    public static readonly Doc BlankLine = new LineDoc(LineKind.Blank);

    /// <summary>Single-line text. Must not contain line breaks: use <see cref="Verbatim"/> for that.</summary>
    public static Doc Text(string text) => new TextDoc(text);

    /// <summary>Text printed exactly as given, which may span lines (preserved statements, block comments).</summary>
    public static Doc Verbatim(string text) => new TextDoc(text);

    public static Doc Concat(params Doc[] parts) => new ConcatDoc([.. parts]);

    public static Doc Concat(IEnumerable<Doc> parts) => new ConcatDoc([.. parts]);

    /// <summary>Line breaks inside are indented one level deeper.</summary>
    public static Doc Nest(Doc doc) => new NestDoc(doc);

    public static Doc Group(Doc doc) => new GroupDoc(doc);
}

internal enum LineKind
{
    Line,
    Soft,
    Hard,
    Blank,
}

internal sealed class TextDoc(string text) : Doc
{
    public string Value { get; } = text;

    /// <summary>Column advance of the text's last line, or -1 when it contains a line break.</summary>
    public int FlatWidth { get; } = text.Contains('\n', StringComparison.Ordinal) ? -1 : text.Length;
}

internal sealed class SpaceDoc : Doc;

internal sealed class LineDoc(LineKind kind) : Doc
{
    public LineKind Kind { get; } = kind;
}

internal sealed class ConcatDoc(ImmutableArray<Doc> parts) : Doc
{
    public ImmutableArray<Doc> Parts { get; } = parts;
}

internal sealed class NestDoc(Doc content) : Doc
{
    public Doc Content { get; } = content;
}

internal sealed class GroupDoc(Doc content) : Doc
{
    public Doc Content { get; } = content;
}

/// <summary>
/// Prints a <see cref="Doc"/>. Indentation is written lazily, so no line ever ends in whitespace the printer
/// wrote, blank lines are empty, and a space never starts a line. Runs in O(n·w): each group's fit test looks
/// ahead at most the remaining line width.
/// </summary>
public static class DocPrinter
{
    private enum Mode
    {
        Flat,
        Break,
    }

    private readonly record struct Command(int Indent, Mode Mode, Doc Doc);

    public static string Print(Doc doc, int width, string indentUnit, string newLine)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(indentUnit);
        var output = new StringBuilder();
        var stack = new List<Command> { new(0, Mode.Break, doc) };
        var column = 0;
        var pendingIndent = 0;
        var atLineStart = true;
        var pendingSpace = false;
        var blankWritten = false;

        void BreakLine(int indent)
        {
            pendingSpace = false;
            pendingIndent = indent;
            column = indent * indentUnit.Length;
            if (atLineStart)
            {
                return; // never two line breaks for one: only BlankLine makes empty lines
            }

            output.Append(newLine);
            atLineStart = true;
            blankWritten = false;
        }

        void Write(TextDoc text)
        {
            if (text.Value.Length == 0)
            {
                return;
            }

            if (atLineStart)
            {
                for (var i = 0; i < pendingIndent; i++)
                {
                    output.Append(indentUnit);
                }

                column = pendingIndent * indentUnit.Length;
            }
            else if (pendingSpace)
            {
                output.Append(' ');
                column++;
            }

            output.Append(text.Value);
            var lastBreak = text.Value.LastIndexOf('\n');
            column = lastBreak < 0 ? column + text.Value.Length : text.Value.Length - lastBreak - 1;
            atLineStart = false;
            pendingSpace = false;
        }

        while (stack.Count > 0)
        {
            var (indent, mode, current) = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            switch (current)
            {
                case TextDoc text:
                    Write(text);
                    break;
                case SpaceDoc:
                    pendingSpace = !atLineStart;
                    break;
                case LineDoc line:
                    switch (line.Kind)
                    {
                        case LineKind.Line when mode == Mode.Flat:
                            pendingSpace = !atLineStart;
                            break;
                        case LineKind.Soft when mode == Mode.Flat:
                            break;
                        case LineKind.Blank:
                            BreakLine(indent);
                            if (output.Length > 0 && !blankWritten)
                            {
                                output.Append(newLine);
                                blankWritten = true;
                            }

                            break;
                        default:
                            BreakLine(indent);
                            break;
                    }

                    break;
                case ConcatDoc concat:
                    for (var i = concat.Parts.Length - 1; i >= 0; i--)
                    {
                        stack.Add(new Command(indent, mode, concat.Parts[i]));
                    }

                    break;
                case NestDoc nest:
                    stack.Add(new Command(indent + 1, mode, nest.Content));
                    break;
                case GroupDoc group:
                    var flat = mode == Mode.Flat
                        || Fits(new Command(indent, Mode.Flat, group.Content), stack, width - column + (pendingSpace ? -1 : 0));
                    stack.Add(new Command(indent, flat ? Mode.Flat : Mode.Break, group.Content));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown document node {current.GetType().Name}.");
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Whether <paramref name="next"/>, printed flat, plus the rest of the line from <paramref name="rest"/> fits
    /// in <paramref name="remaining"/> columns. A forced break inside <paramref name="next"/> never fits.
    /// </summary>
    private static bool Fits(Command next, List<Command> rest, int remaining)
    {
        var local = new Stack<Command>();
        local.Push(next);
        var restIndex = rest.Count - 1;
        while (remaining >= 0)
        {
            Command command;
            bool fromRest;
            if (local.Count > 0)
            {
                command = local.Pop();
                fromRest = false;
            }
            else if (restIndex >= 0)
            {
                command = rest[restIndex--];
                fromRest = true;
            }
            else
            {
                return true;
            }

            switch (command.Doc)
            {
                case TextDoc text:
                    if (text.FlatWidth < 0)
                    {
                        return fromRest; // a multi-line text ends the line; inside the group it forces a break
                    }

                    remaining -= text.FlatWidth;
                    break;
                case SpaceDoc:
                    remaining--;
                    break;
                case LineDoc line:
                    if (command.Mode == Mode.Break || line.Kind is LineKind.Hard or LineKind.Blank)
                    {
                        return fromRest || command.Mode == Mode.Break;
                    }

                    remaining -= line.Kind == LineKind.Line ? 1 : 0;
                    break;
                case ConcatDoc concat:
                    for (var i = concat.Parts.Length - 1; i >= 0; i--)
                    {
                        local.Push(command with { Doc = concat.Parts[i] });
                    }

                    break;
                case NestDoc nest:
                    local.Push(command with { Doc = nest.Content });
                    break;
                case GroupDoc group:
                    local.Push(command with { Doc = group.Content });
                    break;
            }
        }

        return false;
    }
}
