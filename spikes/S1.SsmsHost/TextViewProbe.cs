using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// S1 step 1 (protocol step 2): records the content type of every document editor SSMS opens and confirms caret
/// and edit events arrive. Step 2: registers SQL query buffers with the out-of-process engine.
/// </summary>
[Export(typeof(IWpfTextViewCreationListener))]
[ContentType("any")]
[TextViewRole(PredefinedTextViewRoles.Document)]
internal sealed class TextViewProbe : IWpfTextViewCreationListener
{
    [Import]
    internal ITextDocumentFactoryService DocumentFactory { get; set; } = null!;

    public void TextViewCreated(IWpfTextView textView)
    {
        try
        {
            var buffer = textView.TextBuffer;
            var contentType = buffer.ContentType;
            var chain = string.Join(" > ", BaseTypeChain(contentType));
            var path = DocumentFactory.TryGetTextDocument(buffer, out var document) ? document.FilePath : "(no document)";
            var key = $"{contentType.TypeName}  [{chain}]";
            SpikeLog.ContentTypes.AddOrUpdate(key, 1, (_, count) => count + 1);
            SpikeLog.Info($"View opened: contentType={contentType.TypeName} chain=[{chain}] roles=[{string.Join(",", textView.Roles)}] path={path} length={buffer.CurrentSnapshot.Length}");

            var caretSeen = false;
            var editSeen = false;

            void OnCaret(object sender, CaretPositionChangedEventArgs e)
            {
                if (!caretSeen)
                {
                    caretSeen = true;
                    SpikeLog.Info($"Caret events OK ({contentType.TypeName}): first move to offset {e.NewPosition.BufferPosition.Position}.");
                }
            }

            void OnChanged(object sender, TextContentChangedEventArgs e)
            {
                if (!editSeen)
                {
                    editSeen = true;
                    SpikeLog.Info($"Edit events OK ({contentType.TypeName}): {e.Changes.Count} change(s), new length {e.After.Length}.");
                }
            }

            textView.Caret.PositionChanged += OnCaret;
            buffer.Changed += OnChanged;
            var tracked = contentType.IsOfType("SQL") && TrackDocument(buffer, document?.FilePath);
            textView.Closed += (_, _) =>
            {
                textView.Caret.PositionChanged -= OnCaret;
                buffer.Changed -= OnChanged;
                if (tracked)
                {
                    UntrackDocument(buffer);
                }

                SpikeLog.Info($"View closed: contentType={contentType.TypeName} path={path}");
            };
        }
        catch (Exception exception)
        {
            SpikeLog.Info($"Probe failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// S1 step 2: registers a query buffer with the out-of-process engine (once per buffer; split views share it).
    /// Edits are forwarded as debounced full-text updates; the snapshot travels as the tag so squiggles map back.
    /// </summary>
    private static bool TrackDocument(ITextBuffer buffer, string? filePath)
    {
        var state = buffer.Properties.GetOrCreateSingletonProperty(() => new TrackedDocument());
        if (state.Views++ > 0)
        {
            return true;
        }

        var uri = filePath is { Length: > 0 } ? new Uri(filePath).AbsoluteUri : $"ssms:buffer/{Guid.NewGuid():N}";
        buffer.Properties[EngineHost.DocumentUriKey] = uri;
        state.OnChanged = (_, _) => EngineHost.Engine.Change(uri);
        buffer.Changed += state.OnChanged;
        EngineHost.Engine.Open(uri, () =>
        {
            var snapshot = buffer.CurrentSnapshot;
            return new Engine.DocumentText(snapshot.GetText(), snapshot);
        });
        SpikeLog.Info($"Engine tracking {uri}.");
        return true;
    }

    private static void UntrackDocument(ITextBuffer buffer)
    {
        if (!buffer.Properties.TryGetProperty(typeof(TrackedDocument), out TrackedDocument state) || --state.Views > 0)
        {
            return;
        }

        if (EngineHost.UriOf(buffer) is { } uri)
        {
            buffer.Changed -= state.OnChanged;
            EngineHost.Engine.Close(uri);
            buffer.Properties.RemoveProperty(EngineHost.DocumentUriKey);
            SpikeLog.Info($"Engine closed {uri}.");
        }

        buffer.Properties.RemoveProperty(typeof(TrackedDocument));
    }

    private sealed class TrackedDocument
    {
        public int Views { get; set; }

        public EventHandler<TextContentChangedEventArgs>? OnChanged { get; set; }
    }

    private static IEnumerable<string> BaseTypeChain(IContentType contentType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<IContentType>(contentType.BaseTypes);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current.TypeName))
            {
                continue;
            }

            yield return current.TypeName;
            foreach (var parent in current.BaseTypes)
            {
                queue.Enqueue(parent);
            }
        }
    }
}
