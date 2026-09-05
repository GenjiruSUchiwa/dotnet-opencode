namespace OpenCode.Core.Filesystem;

/// <summary>Reviewed mime-types 3.0.2 / mime-db 1.54.0 lookup subset.
/// Do not infer content types from programming-language names (notably .ts and .rs).</summary>
internal static class FilesystemMime
{
    internal static string Lookup(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".text" or ".conf" or ".def" or ".list" or ".log" or ".in" or ".ini" => "text/plain",
        ".html" or ".htm" or ".shtml" => "text/html",
        ".css" => "text/css",
        ".csv" => "text/csv",
        ".tsv" => "text/tab-separated-values",
        ".js" or ".mjs" => "text/javascript",
        ".jsx" => "text/jsx",
        ".md" or ".markdown" => "text/markdown",
        ".mdx" => "text/mdx",
        ".json" or ".map" => "application/json",
        ".xml" or ".xsl" or ".xsd" or ".rng" => "application/xml",
        ".yaml" or ".yml" => "text/yaml",
        ".toml" => "application/toml",
        ".ts" or ".m2t" or ".m2ts" or ".mts" => "video/mp2t",
        ".rs" => "application/rls-services+xml",
        ".c" or ".cc" or ".cxx" or ".cpp" or ".h" or ".hh" or ".dic" => "text/x-c",
        ".java" => "text/x-java-source",
        ".lua" => "text/x-lua",
        ".s" or ".asm" => "text/x-asm",
        ".scss" => "text/x-scss",
        ".sass" => "text/x-sass",
        ".less" => "text/less",
        ".sh" => "application/x-sh",
        ".png" => "image/png",
        ".jpg" or ".jpeg" or ".jpe" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".svg" or ".svgz" => "image/svg+xml",
        ".ico" => "image/vnd.microsoft.icon",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".wasm" => "application/wasm",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".oga" or ".spx" or ".opus" => "audio/ogg",
        ".mp4" or ".mp4v" or ".mpg4" => "video/mp4",
        ".webm" => "video/webm",
        // These suffixes have no entry in the inspected database. Source lookup falls back.
        "" or "." or ".cs" or ".csproj" or ".razor" or ".tsx" or ".py" or ".go" or ".jsonc"
            or ".props" or ".targets" or ".sln" or ".slnx" or ".ps1" or ".lock" => "application/octet-stream",
        _ => throw new NotSupportedException("This extension needs the complete native mime-types catalog; no substitute Content-Type was returned.")
    };
}
