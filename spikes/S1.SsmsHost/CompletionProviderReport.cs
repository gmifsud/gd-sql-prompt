using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// S1 step 3 evidence: which completion machinery serves SSMS's <c>SQL</c> editor. Modern (async) and legacy
/// completion behave differently when several sources coexist, and a legacy provider can switch the async path
/// off for a whole content type — so we observe before deciding how to integrate.
/// </summary>
internal static class CompletionProviderReport
{
    private static readonly HashSet<string> RelevantContentTypes = new(StringComparer.OrdinalIgnoreCase) { "SQL", "code", "text", "any" };

    public static string Summary { get; private set; } = "(not collected)";

    public static void Collect(IComponentModel componentModel)
    {
        var report = new StringBuilder();
        Add<IAsyncCompletionSourceProvider>(componentModel, "async source", report);
        Add<IAsyncCompletionItemManagerProvider>(componentModel, "async item manager", report);
        Add<IAsyncCompletionCommitManagerProvider>(componentModel, "async commit manager", report);
        Add<ICompletionSourceProvider>(componentModel, "LEGACY source", report);
        Add<IIntellisenseControllerProvider>(componentModel, "LEGACY controller", report);
        Summary = report.Length == 0 ? "(no completion providers matched SQL/code/text/any)" : report.ToString().TrimEnd();
        SpikeLog.Info("Completion providers for SQL editors:" + Environment.NewLine + Summary);
    }

    private static void Add<T>(IComponentModel componentModel, string label, StringBuilder report)
        where T : class
    {
        try
        {
            foreach (var export in componentModel.DefaultExportProvider.GetExports<T, IDictionary<string, object>>())
            {
                var contentTypes = ContentTypes(export.Metadata);
                if (!contentTypes.Any(RelevantContentTypes.Contains))
                {
                    continue;
                }

                string implementation;
                try
                {
                    implementation = export.Value?.GetType().FullName ?? "(null)";
                }
                catch (Exception exception)
                {
                    implementation = $"(failed to create: {exception.GetType().Name})";
                }

                var name = export.Metadata.TryGetValue("Name", out var n) ? n as string : null;
                report.AppendLine($"  {label,-22} {implementation}  [{string.Join(",", contentTypes)}]{(name is null ? string.Empty : "  name=" + name)}");
            }
        }
        catch (Exception exception)
        {
            report.AppendLine($"  {label,-22} (enumeration failed: {exception.GetType().Name}: {exception.Message})");
        }
    }

    private static IReadOnlyList<string> ContentTypes(IDictionary<string, object> metadata)
    {
        if (!metadata.TryGetValue("ContentTypes", out var value))
        {
            return Array.Empty<string>();
        }

        return value switch
        {
            string single => new[] { single },
            IEnumerable<string> many => many.ToList(),
            _ => Array.Empty<string>(),
        };
    }
}
