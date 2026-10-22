using System.Text.RegularExpressions;

namespace SqlCompanion.Spikes.S2;

/// <summary>
/// Rewrites template placeholders so ScriptDom can parse templated scripts, for the oracle only.
/// Every substitution preserves length, so oracle offsets stay aligned with the original text that our
/// parser reads.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>{name}</c> (string-format / application templates) becomes <c>_name_</c>.</item>
/// <item><c>$(name)</c> (SQLCMD variables) becomes <c>__name_</c>.</item>
/// <item>SQLCMD directive lines (<c>:setvar</c>, <c>:r</c>, <c>:on error</c>, …) become comments of the same length.</item>
/// <item>SSMS template parameters <c>&lt;name, type, default&gt;</c> become an identifier of the same length.</item>
/// </list>
/// Identifiers are valid in both expression and name positions, so a placeholder parses wherever a
/// value or object name could appear.
/// </remarks>
internal static partial class Placeholders
{
    public static string Normalise(string text, out int count)
    {
        var replaced = 0;
        text = BracePlaceholder().Replace(text, m => { replaced++; return "_" + m.Groups[1].Value + "_"; });
        text = SqlCmdVariable().Replace(text, m => { replaced++; return "__" + m.Groups[1].Value + "_"; });
        text = SqlCmdDirective().Replace(text, m => { replaced++; return m.Groups[1].Value + "--" + m.Groups[2].Value[1..]; });
        text = SsmsTemplateParameter().Replace(text, m => { replaced++; return "_" + NonIdentifierChar().Replace(m.Value[1..], "_"); });
        count = replaced;
        return text;
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_.]*)\}")]
    private static partial Regex BracePlaceholder();

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_]*)\)")]
    private static partial Regex SqlCmdVariable();

    [GeneratedRegex(@"(?m)^([ \t]*):([A-Za-z]\S*)")]
    private static partial Regex SqlCmdDirective();

    /// <summary>
    /// <c>&lt;Name, type, default&gt;</c>: the name follows <c>&lt;</c> immediately and the type contains no
    /// comparison, so expressions such as <c>a &lt;= 7 … , … &gt;</c> are never matched.
    /// </summary>
    [GeneratedRegex(@"<[A-Za-z_][A-Za-z0-9_ ]*,[^<>=\r\n]*,[^<>=\r\n]*>")]
    private static partial Regex SsmsTemplateParameter();

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex NonIdentifierChar();
}
