namespace OpenCode.Core.Skill;

using System.Text.RegularExpressions;
using OpenCode.Schema;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

internal sealed record SkillObservation(IReadOnlyList<SkillInfo> Skills, bool Available);

/// <summary>Local ConfigSkillPlugin discovery and SkillFile parsing; no URL pull or tool activation.</summary>
internal static class SkillSources
{
    internal static void RequireLocal(IEnumerable<string> sources)
    {
        foreach (var source in sources)
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                throw new NotSupportedException("Remote skill sources require the native skill discovery/pull service.");
    }

    internal static async Task<SkillObservation> ReadAsync(IEnumerable<string> discoveredRoots, IEnumerable<string> configured,
        string location, string home, CancellationToken ct)
    {
        var roots = discoveredRoots.ToList();
        foreach (var source in configured)
        {
            RequireLocal([source]);
            var expanded = source.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, source[2..]) : source;
            // Source path.join normalizes relative entries before Source.equals deduplicates
            // them. A later alias such as skills/. must not override an intervening source.
            roots.Add(Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(location, expanded)));
        }
        var skills = new Dictionary<string, SkillInfo>(StringComparer.Ordinal);
        try
        {
            foreach (var root in roots.Distinct(StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var directory = Path.GetFullPath(root);
                foreach (var file in Files(directory, ct).Order(StringComparer.Ordinal))
                {
                    var content = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                    if (content.Length == 0) continue;
                    var skill = Parse(directory, file, content);
                    if (skill is not null) skills[skill.Id.Value] = skill;
                }
            }
            return new SkillObservation(skills.Values.ToArray(), true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Do not turn a transient read failure into a mass guidance removal.
            // Instruction admission retains its previous value after initialization.
            return new SkillObservation([], false);
        }
    }

    private static SkillInfo? Parse(string directory, string filepath, string content)
    {
        var body = content;
        IDictionary<object, object?> frontmatter = new Dictionary<object, object?>();
        if (content.StartsWith("---\n", StringComparison.Ordinal) || content.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            var start = content.IndexOf('\n') + 1;
            var end = Regex.Match(content[start..], @"(?m)^---\r?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (!end.Success) return null;
            var header = content.Substring(start, end.Index);
            var offset = start + end.Index + end.Length;
            body = content[offset..];
            if (body.StartsWith('\n')) body = body[1..];
            var parsed = ParseYaml(header);
            if (parsed is null) return null;
            frontmatter = parsed;
        }
        if (frontmatter.TryGetValue("name", out var name) && name is not string ||
            frontmatter.TryGetValue("description", out var description) && description is not string ||
            frontmatter.TryGetValue("slash", out var slash) && slash is not bool) return null;
        var id = string.Equals(Path.GetDirectoryName(filepath), directory, StringComparison.Ordinal) && Path.GetFileName(filepath) != "SKILL.md"
            ? Path.GetFileNameWithoutExtension(filepath) : Path.GetFileName(Path.GetDirectoryName(filepath))!;
        frontmatter.TryGetValue("metadata", out var metadata);
        return new SkillInfo(SkillId.FromExisting(id), name as string ?? id, filepath, body,
            description as string,
            MetadataBoolean(metadata, "opencode/slash") ?? slash as bool?,
            MetadataBoolean(metadata, "opencode/autoinvoke"));
    }

    private static IDictionary<object, object?>? ParseYaml(string header)
    {
        object? Read(string value) => new DeserializerBuilder().WithNodeDeserializer(new LiteralScalar()).WithDuplicateKeyChecking()
            .WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object?>(value);
        try { return Mapping(Read(header)); }
        catch (YamlException)
        {
            // ConfigMarkdown.sanitize retries unquoted top-level colon values as literal blocks.
            var sanitized = string.Join("\n", Regex.Split(header, "\r?\n", RegexOptions.NonBacktracking).SelectMany(line =>
            {
                var match = Regex.Match(line, @"^(?<key>[a-zA-Z_][a-zA-Z0-9_]*)\s*:\s*(?<value>.*)$", RegexOptions.NonBacktracking | RegexOptions.ExplicitCapture);
                if (!match.Success) return new[] { line };
                var value = match.Groups["value"].Value.Trim();
                return value.Length > 0 && value is not (">" or "|") && value[0] is not ('\'' or '"') && value.Contains(':')
                    ? new[] { match.Groups["key"].Value + ": |-", "  " + value } : [line];
            }));
            try { return Mapping(Read(sanitized)); }
            catch (YamlException) { return null; }
        }
    }

    private static IDictionary<object, object?>? Mapping(object? value) => value is null
        ? new Dictionary<object, object?>() : value as IDictionary<object, object?>;

    // YamlDotNet's untyped scalar inference excludes quoted/folded strings but
    // not literal blocks. A literal description such as "true" must stay text.
    private sealed class LiteralScalar : INodeDeserializer
    {
        public bool Deserialize(IParser parser, Type expectedType, Func<IParser, Type, object?> nestedObjectDeserializer,
            out object? value, ObjectDeserializer rootDeserializer)
        {
            value = null;
            if (expectedType != typeof(object) || !parser.Accept<Scalar>(out var scalar) || scalar.Style != ScalarStyle.Literal) return false;
            parser.MoveNext();
            value = scalar.Value;
            return true;
        }
    }

    private static bool? MetadataBoolean(object? value, string key)
    {
        if (value is not IDictionary<object, object?> map || !map.TryGetValue(key, out var field)) return null;
        if (field is bool boolean) return boolean;
        // Match JS trim (including BOM, excluding .NET's U+0085) for metadata flags.
        const string whitespace = "\u0009\u000A\u000B\u000C\u000D\u0020\u00A0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
        return field is string text ? text.Trim(whitespace.ToCharArray()).ToLowerInvariant() switch { "true" => true, "false" => false, _ => null } : null;
    }

    private static IReadOnlyList<string> Files(string root, CancellationToken ct)
    {
        var result = new List<string>();
        var visiting = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Walk(new DirectoryInfo(root), root);
        return result;

        void Walk(DirectoryInfo directory, string logical)
        {
            ct.ThrowIfCancellationRequested();
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory.FullName); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.Directory) == (FileAttributes)0) return;
            var physical = directory.ResolveLinkTarget(true) as DirectoryInfo ?? directory;
            if (!visiting.Add(physical.FullName)) return;
            try
            {
                foreach (var child in physical.EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    var path = Path.Combine(logical, child.Name);
                    if ((child.Attributes & FileAttributes.Directory) != (FileAttributes)0)
                        Walk(new DirectoryInfo(child.FullName), path);
                    else if (child.Name == "SKILL.md" || logical == root && child.Name.EndsWith(".md", StringComparison.Ordinal))
                        result.Add(path);
                }
            }
            finally { visiting.Remove(physical.FullName); }
        }
    }
}
