namespace OpenCode.Server.Endpoints;

using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using OpenCode.Core.Database;
using OpenCode.Core.Locations;
using OpenCode.Schema;
using OpenCode.Core.Projects;

/// <summary>Server/location.ts requestRef: query, then headers, then the server directory.
/// This is request placement only. Session routes must keep using the stored Session Location.</summary>
public static class RequestLocation
{
    public static LocationRef Reference(HttpRequest request)
    {
        var workspace = QueryValue(request, "location[workspace]");
        if (string.IsNullOrEmpty(workspace)) workspace = request.Headers["x-opencode-workspace"].ToString();
        var directory = QueryValue(request, "location[directory]");
        if (string.IsNullOrEmpty(directory))
        {
            var header = request.Headers["x-opencode-directory"].ToString();
            directory = header.Length == 0 ? Directory.GetCurrentDirectory() : DecodeDirectory(header);
        }
        return new(directory, string.IsNullOrEmpty(workspace) ? null : WorkspaceId.FromExisting(workspace));
    }

    public static Task<LocationInfo> ResolveAsync(HttpRequest request, IDatabase database, CancellationToken ct = default)
    {
        var reference = Reference(request);
        return ProjectDiscovery.ResolveAsync(database, reference.Directory, reference.WorkspaceId?.Value, ct);
    }

    // URLSearchParams.get is case-sensitive and returns the first occurrence, even if empty.
    // ASP.NET's parsed Query dictionary is case-insensitive, so it is not this boundary.
    public static string? QueryValue(HttpRequest request, string name, bool single = false)
    {
        string? result = null;
        foreach (var pair in new QueryStringEnumerable(request.QueryString.Value))
        {
            if (!pair.DecodeName().Span.SequenceEqual(name)) continue;
            if (result is not null) throw new ArgumentException($"{name} must occur once.");
            result = pair.DecodeValue().ToString();
            if (!single) return result;
        }
        return result;
    }

    // Uri.UnescapeDataString tolerates malformed escapes/UTF-8, unlike decodeURIComponent.
    // Decode each escaped byte run strictly; on any failure preserve the entire header.
    public static string DecodeDirectory(string value, bool strict = false)
    {
        var output = new StringBuilder(value.Length);
        var bytes = new byte[value.Length];
        for (var index = 0; index < value.Length;)
        {
            if (value[index] != '%') { output.Append(value[index++]); continue; }
            var count = 0;
            while (index < value.Length && value[index] == '%')
            {
                if (index + 2 >= value.Length || !char.IsAsciiHexDigit(value[index + 1]) || !char.IsAsciiHexDigit(value[index + 2]))
                    return strict ? throw new ArgumentException("Malformed percent encoding in file path.") : value;
                bytes[count++] = Convert.ToByte(value.Substring(index + 1, 2), 16);
                index += 3;
            }
            try { output.Append(new UTF8Encoding(false, true).GetString(bytes, 0, count)); }
            catch (DecoderFallbackException) { return strict ? throw new ArgumentException("Malformed UTF-8 in file path.") : value; }
        }
        return output.ToString();
    }
}
