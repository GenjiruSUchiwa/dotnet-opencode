namespace OpenCode.Server.Endpoints;

using OpenCode.Schema;

public static class LocationEndpoints
{
    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/location", () =>
        {
            var cwd = Directory.GetCurrentDirectory();
            return Results.Ok(new
            {
                directory = cwd,
                project = new
                {
                    id = "prj_local",
                    directory = cwd,
                    canonical = cwd
                }
            });
        });

        app.MapGet("/api/fs/list", (string? path) =>
        {
            var cwd = Directory.GetCurrentDirectory();
            var targetDir = string.IsNullOrEmpty(path) ? cwd : Path.Combine(cwd, path);

            var entries = new List<FileSystemEntry>();
            if (Directory.Exists(targetDir))
            {
                foreach (var dir in Directory.GetDirectories(targetDir))
                {
                    var name = Path.GetFileName(dir);
                    if (!name.StartsWith('.'))
                    {
                        entries.Add(new FileSystemEntry(name, FileSystemEntryType.Directory));
                    }
                }
                foreach (var file in Directory.GetFiles(targetDir))
                {
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith('.'))
                    {
                        entries.Add(new FileSystemEntry(name, FileSystemEntryType.File));
                    }
                }
            }

            return Results.Ok(new
            {
                location = new
                {
                    directory = cwd,
                    project = new { id = "prj_local", directory = cwd, canonical = cwd }
                },
                data = entries
            });
        });

        app.MapGet("/api/fs/find", (string query, int? limit) =>
        {
            var cwd = Directory.GetCurrentDirectory();
            var entries = new List<FileSystemEntry>();

            if (Directory.Exists(cwd))
            {
                var files = Directory.GetFiles(cwd, $"*{query}*", SearchOption.AllDirectories)
                    .Take(limit ?? 50);

                foreach (var f in files)
                {
                    var rel = Path.GetRelativePath(cwd, f);
                    entries.Add(new FileSystemEntry(rel, FileSystemEntryType.File));
                }
            }

            return Results.Ok(new
            {
                location = new
                {
                    directory = cwd,
                    project = new { id = "prj_local", directory = cwd, canonical = cwd }
                },
                data = entries
            });
        });
    }
}
