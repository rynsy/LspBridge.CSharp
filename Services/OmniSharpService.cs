using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Newtonsoft.Json.Linq;

namespace LspBridge.CSharp.Services;

/// <summary>
/// Manages one OmniSharp Language Server per repo path and exposes LSP helpers.
/// </summary>
public sealed class OmniSharpService : IAsyncDisposable
{
    // Cache: repoPath -> Task<LanguageClient>
    private readonly ConcurrentDictionary<string, Task<LanguageClient>> _clients = new();

    // Await-once tasks for diagnostics keyed by DocumentUri
    private readonly ConcurrentDictionary<DocumentUri, TaskCompletionSource<PublishDiagnosticsParams>>
        _diagAwaiters = new();

    private readonly ILogger<OmniSharpService> _log;

    public OmniSharpService(ILogger<OmniSharpService> log) => _log = log;

    /* ------------ Public API ------------ */

    /// <summary>Return workspace symbols.</summary>
    public async Task<Container<WorkspaceSymbol>?> GetWorkspaceSymbolsAsync(
        string repoPath, string query)
    {
        var client = await GetClientAsync(repoPath);
        return await client.RequestWorkspaceSymbols(
            new WorkspaceSymbolParams { Query = query });
    }

    /// <summary>Await first diagnostics publish for a specific file.</summary>
    public async Task<PublishDiagnosticsParams> GetDiagnosticsAsync(
        string repoPath, string filePath, CancellationToken ct = default)
    {
        var client = await GetClientAsync(repoPath);

        var uri = DocumentUri.FromFileSystemPath(filePath);
        var tcs = new TaskCompletionSource<PublishDiagnosticsParams>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _diagAwaiters[uri] = tcs;

        var text = await File.ReadAllTextAsync(filePath, ct);
        client.TextDocument.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "csharp",
                Version = 1,
                Text = text
            }
        });

        return await tcs.Task.WaitAsync(ct);
    }

    /* ------------ Lifecycle ------------ */

    public async ValueTask DisposeAsync()
    {
        foreach (var kv in _clients.Values)
        {
            var cli = await kv;
            await cli.Shutdown();
        }
    }

    /* ------------ Internal helpers ------------ */

    private Task<LanguageClient> GetClientAsync(string repoPath) =>
        _clients.GetOrAdd(repoPath, CreateClientAsync);

    private async Task<LanguageClient> CreateClientAsync(string repoPath)
    {
        _log.LogInformation("Starting OmniSharp for {Repo}", repoPath);

        var solutionPath = Directory.EnumerateFiles(repoPath, "*.sln").FirstOrDefault()
                         ?? Directory.EnumerateFiles(repoPath, "*.csproj").First();

        var psi = new ProcessStartInfo("/opt/omnisharp/OmniSharp") // correct path/case
        {
            Arguments = $"-lsp -s {solutionPath} --stdio",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        var proc = Process.Start(psi)!;

        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _log.LogWarning(e.Data); };
        proc.BeginErrorReadLine();

        var clientReady = new TaskCompletionSource();        // <-- gate for project load
        var solutionLoaded = new TaskCompletionSource();
        ProgressToken? projectsToken = null;

        var client = LanguageClient.PreInit(opts =>
        {
            opts.WithInput(proc.StandardOutput.BaseStream)
                .WithOutput(proc.StandardInput.BaseStream)
                .WithRootUri(DocumentUri.FromFileSystemPath(Path.GetDirectoryName(solutionPath)!)).WithInitializationOptions(new
                {
                    MsBuild = new { LoadProjectsOnDemand = false }
                })
                .WithLoggerFactory(
                    LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning)))
                .WithWorkspaceFolder(
                    new WorkspaceFolder
                    {
                        Uri = DocumentUri.FromFileSystemPath(Path.GetDirectoryName(solutionPath)!),
                        Name = Path.GetFileName(repoPath)
                    }
                );

            // Global diagnostics handler – fulfil awaiters if someone is waiting
            opts.OnPublishDiagnostics((diag, _) =>
            {
                if (_diagAwaiters.TryRemove(diag.Uri, out var source))
                    source.TrySetResult(diag);
                return Task.CompletedTask;
            });


            opts.OnProgress(p =>
            {
                var kind = p.Value?["kind"]?.Value<string>();
                var title = p.Value?["kind"]?.Value<string>();
                switch (kind)
                {
                    // 1️⃣ start of the MSBuild load
                    case "begin" when title?.StartsWith("Projects", StringComparison.Ordinal) == true:
                        projectsToken = p.Token;                       // remember the token we care about
                        break;

                    // 2️⃣ end of that same progress
                    case "end" when projectsToken != null && Equals(p.Token, projectsToken):
                        solutionLoaded.TrySetResult();
                        break;
                }

                return Task.CompletedTask;   //  <- don’t forget the async contract
            });
        });

        await client.Initialize(CancellationToken.None);
        await solutionLoaded.Task;
        _log.LogInformation("OmniSharp ready for {Repo}", repoPath);
        return client;
    }
}

