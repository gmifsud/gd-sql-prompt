using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// S1 step 1 (protocol step 2): records the content type of every document editor SSMS opens — the key
/// needed to register completion and taggers later — and confirms caret and edit events arrive.
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
            textView.Closed += (_, _) =>
            {
                textView.Caret.PositionChanged -= OnCaret;
                buffer.Changed -= OnChanged;
                SpikeLog.Info($"View closed: contentType={contentType.TypeName} path={path}");
            };
        }
        catch (Exception exception)
        {
            SpikeLog.Info($"Probe failed: {exception.GetType().Name}: {exception.Message}");
        }
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
