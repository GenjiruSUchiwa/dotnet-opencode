namespace OpenCode.Server;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenCode.Core.Database;
using OpenCode.Core.Llm;
using OpenCode.Core.Session;
using OpenCode.Server.Endpoints;
using OpenCode.Server.Services;

public static class ServerHost
{
    public static WebApplication CreateApp(string[] args, int port = 5050)
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

        return app;
    }
}
