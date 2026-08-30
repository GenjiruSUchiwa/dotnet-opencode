namespace OpenCode.Server;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Core.Session;
using OpenCode.Core.Tools;
using OpenCode.Server.Endpoints;
using OpenCode.Server.Services;

public static class ServerHost
{
    public const int DefaultPort = 5055;

    public static string GetRegistrationPath(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath)) return customPath;

        var env = Environment.GetEnvironmentVariable("OPENCODE_SERVICE_FILE");
        if (!string.IsNullOrEmpty(env)) return env;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "opencode-dotnet", "service.json");
    }

    public static WebApplication CreateApp(string[] args, int port = DefaultPort, string? registrationFile = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, port);
        });

        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddSingleton<IDatabase>(_ => new SqliteDatabase());
        builder.Services.AddSingleton<CredentialStore>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<ProviderResolver>();
        builder.Services.AddSingleton<ToolRegistry>();
        builder.Services.AddSingleton<SessionExecutionEngine>();
        builder.Services.AddSingleton<IEventFeedService, EventFeedService>();

        var app = builder.Build();

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
        app.MapFeatureEndpoints();

        // Write daemon registration file
        var regFile = GetRegistrationPath(registrationFile);
        var regDir = Path.GetDirectoryName(regFile)!;
        Directory.CreateDirectory(regDir);

        var serviceInfo = new
        {
            id = Guid.NewGuid().ToString(),
            version = "10.0.0",
            url = $"http://127.0.0.1:{port}",
            pid = Environment.ProcessId
        };
        File.WriteAllText(regFile, JsonSerializer.Serialize(serviceInfo));

        // Clean up on exit
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                if (File.Exists(regFile))
                {
                    File.Delete(regFile);
                }
            }
            catch { }
        });

        return app;
    }
}
