namespace SqlCompanion.Engine.Syntax;

public enum SyntaxKind
{
    None = 0,

    // Special tokens
    EndOfFileToken,
    BadToken,

    // Words
    IdentifierToken,
    QuotedIdentifierToken,
    KeywordToken,
    VariableToken,

    // Literals
    StringLiteralToken,
    NumericLiteralToken,
    BinaryLiteralToken,

    // Punctuation
    OpenParenToken,
    CloseParenToken,
    OpenBraceToken,
    CloseBraceToken,
    CommaToken,
    SemicolonToken,
    DotToken,
    ColonToken,
    ColonColonToken,

    // Arithmetic, bitwise and string operators
    PlusToken,
    MinusToken,
    AsteriskToken,
    SlashToken,
    PercentToken,
    AmpersandToken,
    BarToken,
    BarBarToken,
    CaretToken,
    TildeToken,

    // Comparison operators
    EqualsToken,
    NotEqualsToken,
    LessThanToken,
    LessThanEqualsToken,
    GreaterThanToken,
    GreaterThanEqualsToken,
    NotLessThanToken,
    NotGreaterThanToken,

    // Compound assignment operators
    PlusEqualsToken,
    MinusEqualsToken,
    AsteriskEqualsToken,
    SlashEqualsToken,
    PercentEqualsToken,
    AmpersandEqualsToken,
    BarEqualsToken,
    BarBarEqualsToken,
    CaretEqualsToken,

    // Trivia
    WhitespaceTrivia,
    EndOfLineTrivia,
    SingleLineCommentTrivia,
    MultiLineCommentTrivia,
}
