using System.Collections.Immutable;
using System.Text;

namespace SqlCompanion.Engine.Syntax;

/// <summary>A token or a node in the concrete syntax tree.</summary>
public abstract class SyntaxElement
{
    public abstract SyntaxKind Kind { get; }

    /// <summary>Span of the element's text, excluding the outer leading and trailing trivia.</summary>
    public abstract TextSpan Span { get; }

    /// <summary>Span including all trivia.</summary>
    public abstract TextSpan FullSpan { get; }

    public abstract void WriteTo(StringBuilder builder);

    public string ToFullString()
    {
        var builder = new StringBuilder(FullSpan.Length);
        WriteTo(builder);
        return builder.ToString();
    }
}

/// <summary>
/// A homogeneous, immutable CST node (ADR-0002). Every node owns at least one token, so spans are
/// always defined; the tree as a whole owns every token of the source, including end-of-file.
/// </summary>
public sealed class SyntaxNode : SyntaxElement
{
    public SyntaxNode(SyntaxKind kind, ImmutableArray<SyntaxElement> children)
    {
        if (children.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A syntax node must own at least one element.", nameof(children));
        }

        Kind = kind;
        Children = children;
        FirstToken = children[0] switch
        {
            SyntaxToken token => token,
            SyntaxNode node => node.FirstToken,
            _ => throw new ArgumentException("Unknown element type.", nameof(children)),
        };
        LastToken = children[^1] switch
        {
            SyntaxToken token => token,
            SyntaxNode node => node.LastToken,
            _ => throw new ArgumentException("Unknown element type.", nameof(children)),
        };
    }

    public override SyntaxKind Kind { get; }

    public ImmutableArray<SyntaxElement> Children { get; }

    public SyntaxToken FirstToken { get; }

    public SyntaxToken LastToken { get; }

    public override TextSpan Span => TextSpan.FromBounds(FirstToken.Position, LastToken.Span.End);

    public override TextSpan FullSpan => TextSpan.FromBounds(FirstToken.FullSpan.Start, LastToken.FullSpan.End);

    /// <summary>
    /// True when a caret at <paramref name="position"/> is within the node: from its first token to the end of
    /// its last token's same-line trailing trivia (see <see cref="SyntaxToken.CaretExtentEnd"/>), inclusive.
    /// </summary>
    public bool ContainsCaret(int position) => position >= Span.Start && position <= LastToken.CaretExtentEnd;

    public IEnumerable<SyntaxNode> ChildNodes() => Children.OfType<SyntaxNode>();

    public IEnumerable<SyntaxToken> ChildTokens() => Children.OfType<SyntaxToken>();

    /// <summary>Pre-order traversal of descendant nodes, excluding this node.</summary>
    public IEnumerable<SyntaxNode> DescendantNodes(Func<SyntaxNode, bool>? descendInto = null)
    {
        var stack = new Stack<IEnumerator<SyntaxNode>>();
        stack.Push(ChildNodes().GetEnumerator());
        while (stack.Count > 0)
        {
            var enumerator = stack.Peek();
            if (!enumerator.MoveNext())
            {
                enumerator.Dispose();
                stack.Pop();
                continue;
            }

            var node = enumerator.Current;
            yield return node;
            if (descendInto is null || descendInto(node))
            {
                stack.Push(node.ChildNodes().GetEnumerator());
            }
        }
    }

    public IEnumerable<SyntaxToken> DescendantTokens()
    {
        foreach (var child in Children)
        {
            if (child is SyntaxToken token)
            {
                yield return token;
            }
            else
            {
                foreach (var nested in ((SyntaxNode)child).DescendantTokens())
                {
                    yield return nested;
                }
            }
        }
    }

    public override void WriteTo(StringBuilder builder)
    {
        foreach (var child in Children)
        {
            child.WriteTo(builder);
        }
    }

    public override string ToString() => $"{Kind} {Span}";
}

/// <summary>A parsed document: lossless CST plus lexical and syntactic diagnostics.</summary>
public sealed class SyntaxTree
{
    private SyntaxTree(string text, SyntaxNode root, ImmutableArray<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        Diagnostics = diagnostics;
    }

    public string Text { get; }

    public SyntaxNode Root { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    /// <summary>Parses any input without throwing. <c>Root.ToFullString() == text</c> always holds.</summary>
    public static SyntaxTree Parse(string text)
    {
        var lex = Lexer.Lex(text);
        var (root, parseDiagnostics) = Parser.Parse(lex.Tokens);
        return new SyntaxTree(text, root, lex.Diagnostics.AddRange(parseDiagnostics));
    }
}
