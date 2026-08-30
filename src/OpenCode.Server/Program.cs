using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Core.Session;
using OpenCode.Server.Endpoints;
using OpenCode.Server.Services;

var builder = WebApplication.CreateSlimBuilder(args);

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

app.Run();

public partial class Program { }
