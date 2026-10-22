using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using SqlCompanion.Spikes.S1.Engine;

namespace SqlCompanion.Spikes.S1;

/// <summary>Squiggles for engine diagnostics in SSMS query windows (content type <c>SQL</c>, found in step 1).</summary>
[Export(typeof(ITaggerProvider))]
[ContentType("SQL")]
[TagType(typeof(IErrorTag))]
internal sealed class DiagnosticTaggerProvider : ITaggerProvider
{
    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer)
        where T : ITag =>
        buffer.Properties.GetOrCreateSingletonProperty(() => new DiagnosticTagger(buffer)) as ITagger<T>;
}

/// <summary>
/// Maps diagnostics onto the snapshot they were computed for, then tracks them forward to whatever the user has
/// typed since, so squiggles stay anchored while the engine catches up.
/// </summary>
internal sealed class DiagnosticTagger : ITagger<IErrorTag>
{
    private readonly ITextBuffer _buffer;
    private volatile IReadOnlyList<(ITrackingSpan Span, IErrorTag Tag)> _items = Array.Empty<(ITrackingSpan, IErrorTag)>();

    public DiagnosticTagger(ITextBuffer buffer)
    {
        _buffer = buffer;
        EngineHost.DiagnosticsPublished += OnPublished;
    }

    public event EventHandler<SnapshotSpanEventArgs>? TagsChanged;

    public IEnumerable<ITagSpan<IErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans)
    {
        if (spans.Count == 0)
        {
            yield break;
        }

        var snapshot = spans[0].Snapshot;
        foreach (var (trackingSpan, tag) in _items)
        {
            SnapshotSpan span;
            try
            {
                span = trackingSpan.GetSpan(snapshot);
            }
            catch (ArgumentException)
            {
                continue; // requested snapshot predates the diagnostics; a tagger that throws gets disabled by the editor
            }

            if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(span)))
            {
                yield return new TagSpan<IErrorTag>(span, tag);
            }
        }
    }

    private void OnPublished(PublishedDiagnostics published)
    {
        if (published.Uri != EngineHost.UriOf(_buffer))
        {
            return;
        }

        var snapshot = published.Tag as ITextSnapshot;
        if (snapshot is null || snapshot.TextBuffer != _buffer)
        {
            snapshot = _buffer.CurrentSnapshot;
        }

        var items = new List<(ITrackingSpan, IErrorTag)>();
        foreach (var diagnostic in published.Diagnostics)
        {
            var start = ToPosition(snapshot, diagnostic.StartLine, diagnostic.StartCharacter);
            var end = Math.Max(start, ToPosition(snapshot, diagnostic.EndLine, diagnostic.EndCharacter));
            if (end == start && end < snapshot.Length)
            {
                end++; // zero-width findings still need a visible squiggle
            }

            var errorType = diagnostic.Severity == 1 ? PredefinedErrorTypeNames.SyntaxError : PredefinedErrorTypeNames.Warning;
            items.Add((
                snapshot.CreateTrackingSpan(Span.FromBounds(start, end), SpanTrackingMode.EdgeExclusive),
                new ErrorTag(errorType, $"{diagnostic.Code}: {diagnostic.Message} (SQL Companion)")));
        }

        _items = items;
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var current = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(current, 0, current.Length)));
        });
    }

    private static int ToPosition(ITextSnapshot snapshot, int line, int character)
    {
        if (line >= snapshot.LineCount)
        {
            return snapshot.Length;
        }

        var snapshotLine = snapshot.GetLineFromLineNumber(Math.Max(0, line));
        return snapshotLine.Start.Position + Math.Min(Math.Max(0, character), snapshotLine.Length);
    }
}
