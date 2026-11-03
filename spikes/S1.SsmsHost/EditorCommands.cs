using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// S1 step 2 (write half): replaces the query window's selection through <see cref="ITextBuffer"/> in a single
/// edit, so the editor's undo history records one transaction. The formatter and refactorings will write the same
/// way. No query text is logged.
/// </summary>
internal static class SelectionWriter
{
    public static (bool Ok, string Message) UppercaseSelection(System.IServiceProvider services)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (services.GetService(typeof(SVsTextManager)) is not IVsTextManager textManager
            || textManager.GetActiveView(1, null, out var vsView) != VSConstants.S_OK
            || vsView is null)
        {
            return (false, "Uppercase selection: no active editor.");
        }

        if (services.GetService(typeof(SComponentModel)) is not IComponentModel componentModel)
        {
            return (false, "Uppercase selection: component model unavailable.");
        }

        var view = componentModel.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(vsView);
        if (view is null)
        {
            return (false, "Uppercase selection: the active editor is not a WPF text view.");
        }

        var buffer = view.TextBuffer;
        if (!buffer.ContentType.IsOfType("SQL"))
        {
            return (false, $"Uppercase selection: the active editor is '{buffer.ContentType.TypeName}', not a query window.");
        }

        // Selection spans live in the view's (visual) snapshot; map them to the data buffer before editing.
        var targets = view.Selection.SelectedSpans
            .SelectMany(span => view.BufferGraph.MapDownToBuffer(span, SpanTrackingMode.EdgeExclusive, buffer))
            .Select(span => span.TranslateTo(buffer.CurrentSnapshot, SpanTrackingMode.EdgeExclusive))
            .Where(span => !span.IsEmpty)
            .ToList();
        if (targets.Count == 0)
        {
            return (false, "Uppercase selection: select some text in the query window first.");
        }

        if (targets.Any(span => buffer.IsReadOnly(span.Span)))
        {
            return (false, "Uppercase selection: the selection is read-only (is a query running?); nothing changed.");
        }

        var before = buffer.CurrentSnapshot.Version.VersionNumber;
        var characters = 0;
        using (var edit = buffer.CreateEdit())
        {
            foreach (var span in targets)
            {
                var text = span.GetText();
                characters += text.Length;
                edit.Replace(span.Span, text.ToUpperInvariant());
            }

            if (!edit.HasEffectiveChanges)
            {
                return (true, $"Uppercase selection: {characters} character(s) already uppercase; nothing changed.");
            }

            edit.Apply();
        }

        var after = buffer.CurrentSnapshot.Version.VersionNumber;
        return (true, $"Write OK: {targets.Count} span(s), {characters} character(s) replaced in one edit (buffer v{before} → v{after}). Ctrl+Z should undo it in one step.");
    }
}

/// <summary>
/// S1 step 4: our commands' current key bindings (defaults from the .vsct, or the user's rebinding after
/// Tools → Options → Keyboard), and every other command bound to the same chord or to its first stroke alone —
/// a first-stroke binding would make our chord unreachable in that scope.
/// </summary>
internal static class KeyBindingReport
{
    public static string Describe(EnvDTE.DTE dte, Guid commandSet)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var ours = new List<(string Name, int Id, string[] Bindings)>();
        var others = new List<(string Name, string Binding)>();
        foreach (EnvDTE.Command command in dte.Commands)
        {
            string name;
            string[] bindings;
            bool isOurs;
            int id;
            try
            {
                name = command.Name;
                bindings = (command.Bindings as object[] ?? []).OfType<string>().ToArray();
                isOurs = Guid.TryParse(command.Guid, out var guid) && guid == commandSet;
                id = command.ID;
            }
            catch (Exception exception) when (exception is COMException or ArgumentException)
            {
                continue;
            }

            if (isOurs)
            {
                ours.Add((string.IsNullOrEmpty(name) ? "(unnamed)" : name, id, bindings));
            }
            else if (!string.IsNullOrEmpty(name))
            {
                others.AddRange(bindings.Select(binding => (name, binding)));
            }
        }

        if (ours.Count == 0)
        {
            return "  (no SQL Companion commands found in DTE.Commands)";
        }

        var lines = new List<string>();
        foreach (var (name, id, bindings) in ours.OrderBy(c => c.Id))
        {
            lines.Add($"  {name} (id 0x{id:X4}): {(bindings.Length == 0 ? "no key binding" : string.Join(" | ", bindings))}");
            foreach (var binding in bindings)
            {
                var chord = Chord(binding);
                var firstStroke = chord.Split(',')[0].Trim();
                var clashes = others
                    .Where(o => string.Equals(Chord(o.Binding), chord, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Chord(o.Binding), firstStroke, StringComparison.OrdinalIgnoreCase))
                    .Select(o => $"{o.Name} [{o.Binding}]")
                    .OrderBy(o => o, StringComparer.Ordinal)
                    .ToList();
                lines.Add($"    same chord or first stroke '{firstStroke}' alone: {(clashes.Count == 0 ? "none" : string.Join("; ", clashes))}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>"Text Editor::Ctrl+K, Ctrl+Y" → "Ctrl+K, Ctrl+Y".</summary>
    private static string Chord(string binding)
    {
        var separator = binding.IndexOf("::", StringComparison.Ordinal);
        return (separator < 0 ? binding : binding.Substring(separator + 2)).Trim();
    }
}
