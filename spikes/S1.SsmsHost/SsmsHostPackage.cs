using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio;
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
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
public sealed class SsmsHostPackage : AsyncPackage
{
    public const string PackageGuidString = "0a7f2d1c-c557-490c-b6c3-9667d7032cd1";

    private static readonly Guid CommandSet = new("cf8f5551-6626-4240-a7fd-f1a970fb1b3c");
    private static readonly Guid OutputPaneGuid = new("a3549f22-3a25-4660-afe4-9a3bdd6db1a8");
    private const int DiagnosticsCommandId = 0x0100;

    private string _hostDescription = "(not captured)";

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

        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commands)
        {
            commands.AddCommand(new MenuCommand(OnDiagnostics, new CommandID(CommandSet, DiagnosticsCommandId)));
            SpikeLog.Info("Command registered: Tools > SQL Companion: S1 diagnostics.");
        }
        else
        {
            SpikeLog.Info("FAILED: IMenuCommandService unavailable; command not registered.");
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

    private void OnDiagnostics(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var contentTypes = SpikeLog.ContentTypes.IsEmpty
            ? "  (none yet: open a query window, type, then run this command again)"
            : string.Join(Environment.NewLine, SpikeLog.ContentTypes.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Key}  x{kv.Value}"));

        var engine = EngineHost.IsCreated
            ? EngineHost.Engine.Status
            : "not started yet (open a query window)";

        var summary =
            $"Host: {_hostDescription}{Environment.NewLine}{Environment.NewLine}" +
            $"Editor content types seen:{Environment.NewLine}{contentTypes}{Environment.NewLine}{Environment.NewLine}" +
            $"Engine: {engine}{Environment.NewLine}Engine path: {EngineHost.ServerPath}{Environment.NewLine}{Environment.NewLine}" +
            $"Full log: {SpikeLog.FilePath}";

        SpikeLog.Info("Diagnostics command invoked." + Environment.NewLine + summary);
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
