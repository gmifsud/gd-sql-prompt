using System.Collections.Immutable;

namespace SqlCompanion.Engine.Formatting;

/// <summary><see cref="IFormattingService"/> over <see cref="SqlFormatter"/>: minimal edits, document or range (FMT-001).</summary>
public sealed class SqlFormattingService(StyleProfile profile) : IFormattingService
{
    public StyleProfile Profile { get; } = profile ?? throw new ArgumentNullException(nameof(profile));

    public ValueTask<ImmutableArray<TextEdit>> FormatAsync(DocumentSnapshot document, TextSpan? range, FormattingOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        var profile = options is null ? Profile : Profile with { IndentSize = Math.Max(1, options.TabSize), UseTabs = !options.InsertSpaces };
        return ValueTask.FromResult(SqlFormatter.ComputeEdits(document.Text, profile, range));
    }
}
