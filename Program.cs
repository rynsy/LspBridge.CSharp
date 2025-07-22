using LspBridge.CSharp.Services;
using LspBridge.CSharp.Tools;

var builder = WebApplication.CreateBuilder(args);


builder.Services
    .AddMcpServer()
    .WithToolsFromAssembly(typeof(LspBridgeTools).Assembly)
    .WithHttpTransport();

builder.Services.AddSingleton<OmniSharpService>();

var app = builder.Build();
app.MapMcp(); // or use app.MapPost("/symbols", ...) for REST-style
app.Run();