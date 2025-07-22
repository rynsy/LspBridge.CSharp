using LspBridge.CSharp.Services;
using ModelContextProtocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LspBridge.CSharp.Tools;

public record SymbolRequest(string RepoPath, string Query);

[McpServerToolType]                      // expose all methods in class
public class LspBridgeTools(OmniSharpService omni)
{
    [McpServerTool]
    public Task<Container<WorkspaceSymbol>?> GetSymbolsAsync(SymbolRequest r)
      => omni.GetWorkspaceSymbolsAsync(r.RepoPath, r.Query);

    [McpServerTool(Name = "get_diagnostics")]
    public Task<PublishDiagnosticsParams> GetDiagnosticsAsync(
        string repoPath, string file)
      => omni.GetDiagnosticsAsync(repoPath, file);
}

