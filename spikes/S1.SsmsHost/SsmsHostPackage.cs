using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace SqlCompanion.Spikes.S1;

/// <summary>
/// S1 step 1: proves the package loads in SSMS, records host facts, and exposes one command.
/// Loads in the background once the shell is initialised, so it never delays SSMS start-up.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideMenuResource("Menus.ctmenu", 2)] // bumped so SSMS re-merges menus and default key bindings
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class SsmsHostPackage : AsyncPackage
{
    public const string PackageGuidString = "0a7f2d1c-c557-490c-b6c3-9667d7032cd1";

    private static readonly Guid CommandSet = new("cf8f5551-6626-4240-a7fd-f1a970fb1b3c");
    private static readonly Guid OutputPaneGuid = new("a3549f22-3a25-4660-afe4-9a3bdd6db1a8");
    private const int DiagnosticsCommandId = 0x0100;
    private const int CompletionModeCommandId = 0x0101;
    private const int UppercaseSelectionCommandId = 0x0102;

    private string _hostDescription = "(not captured)";
    private CompletionModeController? _modeController;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await GetServiceAsync(typeof(SVsOutputWindow)) is IVsOutputWindow output)
        {
            var paneGuid = OutputPaneGuid;
            output.CreatePane(ref paneGuid, "SQL Companion (S1)", fInitVisible: 1, fClearWithSolution: 0);
            output.GetPane(ref paneGuid, out var pane);
            SpikeLog.Pane = pane;
        }

        _hostDescription = await DescribeHostAsync();
        SpikeLog.Info("Package loaded. " + _hostDescription);

        if (await GetServiceAsync(typeof(SComponentModel)) is IComponentModel componentModel)
        {
            CompletionProviderReport.Collect(componentModel);
        }
        else
        {
            SpikeLog.Info("FAILED: SComponentModel unavailable; completion providers not enumerated.");
        }

        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand(OnDiagnostics, new CommandID(CommandSet, DiagnosticsCommandId)));
            commands.AddCommand(new MenuCommand(OnCycleCompletionMode, new CommandID(CommandSet, CompletionModeCommandId)));
            commands.AddCommand(new MenuCommand(OnUppercaseSelection, new CommandID(CommandSet, UppercaseSelectionCommandId)));
            SpikeLog.Info($"Commands registered: Tools > SQL Companion: S1 diagnostics / completion mode (current: {CompletionModeSettings.Mode}) / uppercase selection.");
        }
        else
        {
            SpikeLog.Info("FAILED: IMenuCommandService unavailable; commands not registered.");
        }

        if (await GetServiceAsync(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
        {
            _modeController = new CompletionModeController(this, dte, JoinableTaskFactory);
            if (CompletionModeSettings.Mode == CompletionMode.Replace)
            {
                _modeController.ApplyToActiveWindow("package load"); // windows opened before the background load
            }
        }
        else
        {
            SpikeLog.Info("FAILED: DTE unavailable; completion mode cannot be applied to query windows.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && EngineHost.IsCreated)
        {
            // Polite shutdown; the job object kills the engine anyway if SSMS exits abnormally.
            EngineHost.Engine.Dispose();
        }

        base.Dispose(disposing);
    }

    private async System.Threading.Tasks.Task<string> DescribeHostAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        var description = new StringBuilder();
        try
        {
            var process = Process.GetCurrentProcess();
            var module = process.MainModule;
            description.Append($"Host={module?.FileName} FileVersion={module?.FileVersionInfo.FileVersion} ");
            description.Append($"ProductVersion={module?.FileVersionInfo.ProductVersion} ");
            description.Append($"Runtime={RuntimeInformation.FrameworkDescription} {(Environment.Is64BitProcess ? "x64" : "x86")} ");

            if (await GetServiceAsync(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
            {
                description.Append($"DTE.Name={dte.Name} DTE.Version={dte.Version} DTE.Edition={dte.Edition}");
            }
            else
            {
                description.Append("DTE=unavailable");
            }
        }
        catch (Exception exception)
        {
            description.Append($"(host description failed: {exception.GetType().Name}: {exception.Message})");
        }

        return description.ToString();
    }

    /// <summary>
    /// Cycles Off → Replace → MergeExperiment → Off and applies the new mode to the active query window; other query
    /// windows follow as they are activated (SSMS's IntelliSense toggle is per window).
    /// </summary>
    private void OnCycleCompletionMode(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_modeController is null)
        {
            SpikeLog.Info("FAILED: completion mode controller unavailable (DTE missing); mode unchanged.");
            return;
        }

        var from = CompletionModeSettings.Mode;
        var to = from switch
        {
            CompletionMode.Off => CompletionMode.Replace,
            CompletionMode.Replace => CompletionMode.MergeExperiment,
            _ => CompletionMode.Off,
        };

        CompletionModeSettings.Save(to);
        SpikeLog.Info($"Completion mode: {from} → {to}.");
        var applied = _modeController.ApplyToActiveWindow("mode change");
        var message =
            $"Completion mode: {from} → {to}.{Environment.NewLine}{applied}{Environment.NewLine}{_modeController.Status}.{Environment.NewLine}{Environment.NewLine}" +
            to switch
            {
                CompletionMode.Replace => "SQL Companion completion is active. SSMS IntelliSense is switched off in each query window as you activate it.",
                CompletionMode.MergeExperiment => "SSMS IntelliSense on (windows we switched off are restored as you activate them); SQL Companion items appear as an extra tab in SSMS's list.",
                _ => "SSMS IntelliSense only (windows we switched off are restored as you activate them); SQL Companion completion inactive.",
            };

        VsShellUtilities.ShowMessageBox(this, message, "SQL Companion - completion mode", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    /// <summary>Step 2 (write) and step 4 (key binding): replaces the selection in one edit; Ctrl+Z must undo it in one step.</summary>
    private void OnUppercaseSelection(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var (ok, message) = SelectionWriter.UppercaseSelection(this);
        SpikeLog.Info(message);
        if (ok && GetService(typeof(EnvDTE.DTE)) is EnvDTE.DTE dte)
        {
            dte.StatusBar.Text = message;
            return;
        }

        VsShellUtilities.ShowMessageBox(this, message, "SQL Companion - uppercase selection", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    private void OnDiagnostics(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var contentTypes = SpikeLog.ContentTypes.IsEmpty
            ? "  (none yet: open a query window, type, then run this command again)"
            : string.Join(Environment.NewLine, SpikeLog.ContentTypes.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key}  x{kv.Value}"));

        var engine = EngineHost.IsCreated
            ? EngineHost.Engine.Status
            : "not started yet (open a query window)";

        var dte = GetService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
        var ssmsIntelliSense = (dte is null ? null : SsmsIntelliSense.IsEnabled(this, dte)) switch
        {
            true => "on",
            false => "off",
            null => "unknown (activate a query window first)",
        };
        var intelliSenseCommands = dte is null ? "  (DTE unavailable)" : SsmsIntelliSense.DescribeCommands(dte);

        var bindings = dte is null ? "  (DTE unavailable)" : KeyBindingReport.Describe(dte, CommandSet);

        string Summary(bool redact) =>
            $"Host: {_hostDescription}{Environment.NewLine}{Environment.NewLine}" +
            $"Completion mode: {CompletionModeSettings.Mode} · SSMS IntelliSense (active window): {ssmsIntelliSense} · {_modeController?.Status ?? "mode controller unavailable"}{Environment.NewLine}" +
            $"SSMS IntelliSense commands:{Environment.NewLine}{intelliSenseCommands}{Environment.NewLine}{Environment.NewLine}" +
            $"Editor content types seen:{Environment.NewLine}{contentTypes}{Environment.NewLine}{Environment.NewLine}" +
            $"Engine: {engine}{Environment.NewLine}Engine path: {EngineHost.ServerPath}{Environment.NewLine}{Environment.NewLine}" +
            $"Completion: {CompletionStats.Summary}{Environment.NewLine}Providers for SQL editors:{Environment.NewLine}{CompletionProviderReport.Summary}{Environment.NewLine}{Environment.NewLine}" +
            $"Commands and key bindings (step 4):{Environment.NewLine}{bindings}{Environment.NewLine}{Environment.NewLine}" +
            $"Active connection (step 6):{Environment.NewLine}{ConnectionProbe.Describe(dte, redact)}{Environment.NewLine}{Environment.NewLine}" +
            $"Full log: {SpikeLog.FilePath}";

        // Server, database and user names appear in the dialog only; the log (which gets shared) is redacted.
        SpikeLog.Info("Diagnostics command invoked." + Environment.NewLine + Summary(redact: true));
        var summary = Summary(redact: false);
        SpikeLog.Pane?.Activate();
        VsShellUtilities.ShowMessageBox(
            this,
            summary,
            "SQL Companion - S1 diagnostics",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
