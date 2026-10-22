using System.Collections.Immutable;
using SqlCompanion.Engine.Syntax;

namespace SqlCompanion.Engine.Analysis;

/// <summary>
/// ANA-001 seed: reports lexical diagnostics. Parser diagnostics and the configurable rule
/// framework (ANA-005, ANA-007) are added in Phase 1.
/// </summary>
public sealed class SyntaxDiagnosticService : IDiagnosticService
{
    public ValueTask<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(DocumentSnapshot document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Lexer.Lex(document.Text).Diagnostics);
    }
}
