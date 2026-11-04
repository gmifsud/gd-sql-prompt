using System.Text;

namespace SqlCompanion.Cli;

/// <summary>
/// A SQL file read with its encoding, so rewriting it keeps the encoding and byte-order mark (SSMS saves UTF-8
/// with a BOM by default, and some scripts are UTF-16).
/// </summary>
public sealed class SourceFile
{
    private SourceFile(string path, string text, Encoding encoding)
    {
        Path = path;
        Text = text;
        Encoding = encoding;
    }

    public string Path { get; }

    public string Text { get; }

    public Encoding Encoding { get; }

    public static bool TryRead(string path, out SourceFile file)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var encoding = DetectEncoding(bytes);
            var preamble = encoding.GetPreamble().Length;
            file = new SourceFile(path, encoding.GetString(bytes, preamble, bytes.Length - preamble), encoding);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Console.Error.WriteLine($"sqlwc: cannot read '{path}': {exception.Message}");
            file = new SourceFile(path, string.Empty, Encoding.UTF8);
            return false;
        }
    }

    public bool TryWrite(string text)
    {
        try
        {
            var preamble = Encoding.GetPreamble();
            var body = Encoding.GetBytes(text);
            using var stream = File.Create(Path);
            stream.Write(preamble);
            stream.Write(body);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"sqlwc: cannot write '{Path}': {exception.Message}");
            return false;
        }
    }

    /// <summary>From the byte-order mark; without one, UTF-8 (no BOM written back).</summary>
    public static Encoding DetectEncoding(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        [0xFF, 0xFE, ..] => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        [0xFE, 0xFF, ..] => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };
}
