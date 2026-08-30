using System.Net;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Core.Session;
using OpenCode.Server.Endpoints;
using OpenCode.Server.Services;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure Kestrel on port 5050 or environment port
var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5050;
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, port);
});

// Add Services
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IDatabase>(_ => new SqliteDatabase());
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ProviderResolver>();
builder.Services.AddSingleton<SessionExecutionEngine>();
builder.Services.AddSingleton<IEventFeedService, EventFeedService>();

var app = builder.Build();

// Map Endpoint Groups
app.MapHealthEndpoints();
app.MapEventEndpoints();
app.MapSessionEndpoints();
app.MapProviderEndpoints();
app.MapLocationEndpoints();
app.MapConfigEndpoints();
app.MapAgentEndpoints();
app.MapModelEndpoints();
app.MapPermissionEndpoints();
app.MapPtyEndpoints();

app.Run();

public partial class Program { }
