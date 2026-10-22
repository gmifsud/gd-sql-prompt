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

    // Nodes: structure
    CompilationUnit,
    Batch,
    BatchSeparator,
    ErrorNode,

    // Nodes: statements
    EmptyStatement,
    LabelStatement,
    SelectStatement,
    InsertStatement,
    UpdateStatement,
    DeleteStatement,
    MergeStatement,
    DeclareStatement,
    SetStatement,
    IfStatement,
    ElseClause,
    WhileStatement,
    BlockStatement,
    TryCatchStatement,
    CreateModuleStatement,
    ModuleHeader,
    SimpleStatement,
    OpaqueStatement,

    // Nodes: queries
    WithClause,
    CommonTableExpression,
    QueryExpression,
    QuerySpecification,
    SelectClause,
    TopClause,
    IntoClause,
    FromClause,
    JoinClause,
    OnClause,
    WhereClause,
    GroupByClause,
    HavingClause,
    OrderByClause,
    OffsetClause,
    ForClause,
    OptionClause,

    // Nodes: table sources
    NamedTableSource,
    DerivedTableSource,
    FunctionTableSource,
    VariableTableSource,
    ParenthesizedTableSource,
    Alias,
    MultipartName,

    // Nodes: DML clauses
    DmlTarget,
    SetClause,
    OutputClause,
    InsertColumnList,
    ValuesClause,
    MergeUsingClause,
    MergeWhenClause,

    // Nodes: expressions (shallow)
    Expression,
    ParenthesizedExpression,
    Subquery,
}
