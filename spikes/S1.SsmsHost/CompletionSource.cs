using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Core.Imaging;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Adornments;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// S1 step 3: contributes engine completion items to SSMS query windows through the modern async completion
/// API, alongside SSMS's own IntelliSense. Whether the editor ever calls us — and whether the two lists merge —
/// is the coexistence evidence this step exists to collect, so every first call is logged.
/// </summary>
[Export(typeof(IAsyncCompletionSourceProvider))]
[ContentType("SQL")]
[Name("SqlCompanion.S1.Completion")]
internal sealed class CompletionSourceProvider : IAsyncCompletionSourceProvider
{
    public IAsyncCompletionSource GetOrCreate(ITextView textView)
    {
        CompletionStats.Note("GetOrCreate (provider asked for a source)");
        return textView.Properties.GetOrCreateSingletonProperty(() => new CompletionSource());
    }
}

internal sealed class CompletionSource : IAsyncCompletionSource
{
    private const string Suffix = "SQL Companion";

    private static readonly ImageElement KeywordIcon = new(new ImageId(KnownMonikers.IntellisenseKeyword.Guid, KnownMonikers.IntellisenseKeyword.Id), "Keyword");
    private static readonly ImageElement VariableIcon = new(new ImageId(KnownMonikers.LocalVariable.Guid, KnownMonikers.LocalVariable.Id), "Variable");

    public CompletionStartData InitializeCompletion(CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
    {
        CompletionStats.Note($"InitializeCompletion (reason {trigger.Reason})");

        var typed = trigger.Character;
        var participates = trigger.Reason switch
        {
            CompletionTriggerReason.Invoke or CompletionTriggerReason.InvokeAndCommitIfUnique => true,
            CompletionTriggerReason.Insertion => char.IsLetter(typed) || typed is '@' or '_' or '#',
            _ => false,
        };

        if (!participates)
        {
            return CompletionStartData.DoesNotParticipateInCompletion;
        }

        return new CompletionStartData(CompletionParticipation.ProvidesItems, WordBefore(triggerLocation));
    }

    public async Task<CompletionContext> GetCompletionContextAsync(
        IAsyncCompletionSession session,
        CompletionTrigger trigger,
        SnapshotPoint triggerLocation,
        SnapshotSpan applicableToSpan,
        CancellationToken token)
    {
        var buffer = triggerLocation.Snapshot.TextBuffer;
        if (EngineHost.UriOf(buffer) is not { } uri)
        {
            CompletionStats.Note("GetCompletionContextAsync for an untracked buffer");
            return CompletionContext.Empty;
        }

        var line = triggerLocation.GetContainingLine();
        var stopwatch = Stopwatch.StartNew();
        var items = await EngineHost.Engine.CompleteAsync(uri, line.LineNumber, triggerLocation.Position - line.Start.Position, token)
            .ConfigureAwait(false);
        CompletionStats.Record(stopwatch.Elapsed.TotalMilliseconds, items.Count);

        var completionItems = items
            .Select(i => new CompletionItem(
                displayText: i.Label,
                source: this,
                icon: i.Kind == 6 ? VariableIcon : KeywordIcon,
                filters: ImmutableArray<CompletionFilter>.Empty,
                suffix: Suffix,
                insertText: i.Label,
                sortText: (i.Kind == 6 ? "0" : "1") + i.Label,
                filterText: i.Label,
                attributeIcons: ImmutableArray<ImageElement>.Empty))
            .ToImmutableArray();

        return new CompletionContext(completionItems);
    }

    public Task<object> GetDescriptionAsync(IAsyncCompletionSession session, CompletionItem item, CancellationToken token) =>
        Task.FromResult<object>($"{item.DisplayText} — from the SQL Companion engine");

    /// <summary>The identifier being typed, ending at the caret (the span the session filters and replaces).</summary>
    private static SnapshotSpan WordBefore(SnapshotPoint point)
    {
        var start = point.Position;
        var line = point.GetContainingLine();
        while (start > line.Start.Position)
        {
            var c = point.Snapshot[start - 1];
            if (!(char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$'))
            {
                break;
            }

            start--;
        }

        return new SnapshotSpan(point.Snapshot, Span.FromBounds(start, point.Position));
    }
}

/// <summary>Completion evidence: first occurrence of each call kind, plus keystroke-to-items latency.</summary>
internal static class CompletionStats
{
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    private static readonly List<double> LatenciesMs = [];
    private static readonly object Gate = new();

    public static void Note(string what)
    {
        lock (Gate)
        {
            if (!Seen.Add(what))
            {
                return;
            }
        }

        SpikeLog.Info($"Completion: first {what}.");
    }

    public static void Record(double milliseconds, int itemCount)
    {
        int count;
        lock (Gate)
        {
            LatenciesMs.Add(milliseconds);
            count = LatenciesMs.Count;
        }

        if (count <= 3 || count % 25 == 0)
        {
            SpikeLog.Info($"Completion #{count}: {itemCount} item(s) in {milliseconds:0} ms. {Summary}");
        }
    }

    public static string Summary
    {
        get
        {
            lock (Gate)
            {
                if (LatenciesMs.Count == 0)
                {
                    return "no completion requests yet";
                }

                var sorted = LatenciesMs.OrderBy(x => x).ToList();
                double At(double p) => sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * sorted.Count))];
                return $"completion requests n={sorted.Count} p50={At(0.5):0} ms p95={At(0.95):0} ms max={sorted[sorted.Count - 1]:0} ms";
            }
        }
    }
}
