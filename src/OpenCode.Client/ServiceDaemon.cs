namespace OpenCode.Client;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record ServiceInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("password")] string? Password = null
);

public sealed record ServiceEndpoint(
    string Url,
    string? Password = null
)
{
    public void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(Password))
        {
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", header);
        }
    }
}

/// <summary>
/// 1:1 port of packages/client/src/promise/service.ts
/// Ensures 1 and only 1 daemon is running for opencode-dotnet.
/// </summary>
public static class ServiceDaemon
{
    public const int DefaultPort = 5055;
    public const string DefaultVersion = "10.0.0";

    public static string GetDefaultRegistrationFile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "opencode-dotnet", "service.json");
    }

    public static async Task<ServiceEndpoint?> DiscoverAsync(string? registrationFile = null, CancellationToken ct = default)
    {
        var file = registrationFile ?? GetDefaultRegistrationFile();
        if (!File.Exists(file)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var info = JsonSerializer.Deserialize<ServiceInfo>(json);
            if (info is null) return null;

            // Check if process is still running
            try
            {
                var process = Process.GetProcessById(info.Pid);
                if (process.HasExited) return null;
            }
            catch
            {
                return null;
            }

            // Ping health endpoint
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var endpoint = new ServiceEndpoint(info.Url, info.Password);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Url.TrimEnd('/')}/api/health");
            endpoint.ApplyAuth(req);

            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                return endpoint;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static async Task<ServiceEndpoint> EnsureAsync(
        string? registrationFile = null,
        int port = DefaultPort,
        CancellationToken ct = default)
    {
        var file = registrationFile ?? GetDefaultRegistrationFile();

        // 1. Check if an active compatible server is already running
        var existing = await DiscoverAsync(file, ct);
        if (existing is not null)
        {
            return existing;
        }

        // 2. Clean up stale registration file if present
        if (File.Exists(file))
        {
            try { File.Delete(file); } catch { }
        }

        // 3. Spawn background daemon process
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.Environment["OPENCODE_SERVICE_FILE"] = file;
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(GetServerProjectPath());
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--service");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to launch opencode-dotnet daemon process.");

        // 4. Poll until the daemon registers and becomes healthy
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct);

            var discovered = await DiscoverAsync(file, ct);
            if (discovered is not null)
            {
                return discovered;
            }
        }

        throw new TimeoutException($"Timed out waiting for opencode-dotnet daemon to become ready on port {port}.");
    }

    public static async Task StopAsync(string? registrationFile = null, CancellationToken ct = default)
    {
        var file = registrationFile ?? GetDefaultRegistrationFile();
        if (!File.Exists(file)) return;

        try
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var info = JsonSerializer.Deserialize<ServiceInfo>(json);
            if (info is not null)
            {
                try
                {
                    var process = Process.GetProcessById(info.Pid);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(ct);
                }
                catch { }
            }
            File.Delete(file);
        }
        catch { }
    }

    private static string GetServerProjectPath()
    {
        var candidate = @"C:\Repos\hona\opencode-dotnet\src\OpenCode.Server\OpenCode.Server.csproj";
        if (File.Exists(candidate)) return candidate;
        return "src/OpenCode.Server";
    }
}
