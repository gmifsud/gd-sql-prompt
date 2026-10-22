using SqlCompanion.Server.Lsp;

// stdio transport: stdout carries protocol messages only. Diagnostics/logging must go to stderr.
using var stdout = Console.OpenStandardOutput();
using var stdin = Console.OpenStandardInput();

var (rpc, server) = LanguageServerHost.Start(stdout, stdin);

var finished = await Task.WhenAny(server.ExitCode, rpc.Completion).ConfigureAwait(false);
rpc.Dispose();

// Per LSP: exit code 0 only if 'shutdown' preceded 'exit'; a dropped connection is abnormal.
return finished == server.ExitCode ? await server.ExitCode.ConfigureAwait(false) : 1;
