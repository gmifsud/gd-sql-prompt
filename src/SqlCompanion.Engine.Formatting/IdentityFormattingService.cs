using System.Collections.Immutable;

namespace SqlCompanion.Engine.Formatting;

/// <summary>
/// Phase 0 placeholder: returns no edits. Trivially satisfies the formatter invariants
/// (idempotence, token-stream preservation) so hosts can be wired end to end before the
/// Wadler-style layout engine lands in Phase 1.
/// </summary>
public sealed class IdentityFormattingService : IFormattingService
{
    public ValueTask<ImmutableArray<TextEdit>> FormatAsync(DocumentSnapshot document, TextSpan? range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ImmutableArray<TextEdit>.Empty);
    }
}
