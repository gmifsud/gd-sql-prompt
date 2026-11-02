using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// S1 merge experiment: SSMS's T-SQL IntelliSense reaches the editor through the legacy completion shims, whose
/// sessions ask every legacy <see cref="ICompletionSource"/> for the content type to augment them. If SSMS's
/// sessions call us, our items can appear as an extra "SQL Companion" tab inside SSMS's own list.
/// The first call is logged in every mode (that alone answers "is merging possible?"); items are added only in
/// <see cref="CompletionMode.MergeExperiment"/>.
/// </summary>
[Export(typeof(ICompletionSourceProvider))]
[ContentType("SQL")]
[Name("SqlCompanion.S1.LegacyCompletion")]
internal sealed class LegacyCompletionSourceProvider : ICompletionSourceProvider
{
    public ICompletionSource TryCreateCompletionSource(ITextBuffer textBuffer) =>
        textBuffer.Properties.GetOrCreateSingletonProperty(() => new LegacyCompletionSource(textBuffer));
}

internal sealed class LegacyCompletionSource : ICompletionSource
{
    /// <summary>Legacy sessions are synchronous on the UI thread: never wait longer than this for the engine.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(150);

    private readonly ITextBuffer _buffer;

    public LegacyCompletionSource(ITextBuffer buffer) => _buffer = buffer;

    public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
    {
        CompletionStats.Note($"legacy AugmentCompletionSession from SSMS's session (mode {CompletionModeSettings.Mode}, {completionSets.Count} existing set(s))");
        if (CompletionModeSettings.Mode != CompletionMode.MergeExperiment || EngineHost.UriOf(_buffer) is not { } uri)
        {
            return;
        }

        var snapshot = _buffer.CurrentSnapshot;
        if (session.GetTriggerPoint(snapshot) is not { } trigger)
        {
            return;
        }

        var line = trigger.GetContainingLine();
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<Engine.EngineCompletionItem> items;
        using (var timeout = new CancellationTokenSource(Budget))
        {
            items = ThreadHelper.JoinableTaskFactory.Run(() =>
                EngineHost.Engine.CompleteAsync(uri, line.LineNumber, trigger.Position - line.Start.Position, timeout.Token));
        }

        CompletionStats.Record(stopwatch.Elapsed.TotalMilliseconds, items.Count);
        if (items.Count == 0)
        {
            return;
        }

        var start = trigger.Position;
        while (start > line.Start.Position && IsWordChar(snapshot[start - 1]))
        {
            start--;
        }

        var applicableTo = snapshot.CreateTrackingSpan(Span.FromBounds(start, trigger.Position), SpanTrackingMode.EdgeInclusive);
        var completions = items.Select(i => new Completion(i.Label, i.Label, $"{i.Label} — from the SQL Companion engine", null, "SQL Companion"));
        completionSets.Add(new CompletionSet("SqlCompanion", "SQL Companion", applicableTo, completions, Enumerable.Empty<Completion>()));
    }

    public void Dispose()
    {
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';
}
