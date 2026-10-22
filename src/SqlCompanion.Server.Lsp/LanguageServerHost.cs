using System.Text.Json;
using Microsoft.VisualStudio.Threading;
using SqlCompanion.Engine.Analysis;
using SqlCompanion.Engine.Binding;
using SqlCompanion.Engine.Formatting;
using StreamJsonRpc;

namespace SqlCompanion.Server.Lsp;

public static class LanguageServerHost
{
    /// <summary>
    /// Wires a <see cref="LanguageServer"/> to a header-delimited JSON-RPC connection and starts listening.
    /// Messages are dispatched one at a time, in arrival order, so <c>didChange</c> notifications
    /// are applied in the sequence the client sent them.
    /// </summary>
    public static (JsonRpc Rpc, LanguageServer Server) Start(Stream sending, Stream receiving)
    {
        var server = new LanguageServer(
            new KeywordCompletionService(),
            new IdentityFormattingService(),
            new SyntaxDiagnosticService());

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(sending, receiving, CreateFormatter()))
        {
            SynchronizationContext = new NonConcurrentSynchronizationContext(sticky: false),
        };

        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
        server.Client = rpc;
        rpc.StartListening();
        return (rpc, server);
    }

    public static SystemTextJsonFormatter CreateFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        return formatter;
    }
}
