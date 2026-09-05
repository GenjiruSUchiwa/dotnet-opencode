namespace OpenCode.Core.Worktrees;

using OpenCode.Core.Locations;

internal static class WorktreePaths
{
    internal static string Canonical(string input)
    {
        var full = Path.GetFullPath(LocalToolLocation.WindowsPath(input.Length == 0 ? "." : input));
        if (!Directory.Exists(full)) throw new WorktreeException($"Worktree directory unavailable: {input}");
        var root = Path.GetPathRoot(full)!;
        var current = OperatingSystem.IsWindows() && root.Length >= 2 && root[1] == ':' ? char.ToUpperInvariant(root[0]) + root[1..] : root;
        foreach (var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = OperatingSystem.IsWindows()
                ? Directory.EnumerateDirectories(current).FirstOrDefault(path => Path.GetFileName(path).Equals(segment, StringComparison.OrdinalIgnoreCase))
                    ?? throw new WorktreeException($"Worktree directory unavailable: {input}")
                : Path.Combine(current, segment);
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null) current = directory.ResolveLinkTarget(true)?.FullName
                ?? throw new WorktreeException($"Worktree directory unavailable: {input}");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    // Node path.join appends rather than discarding its first argument when the
    // caller's name starts with a slash. Do not sanitize or trim the supplied name.
    internal static string Join(string parent, string name)
    {
        var joined = (name.Length == 0 ? parent : parent.Length == 0 ? name : parent + Path.DirectorySeparatorChar + name)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var root = Path.GetPathRoot(joined) ?? "";
        var segments = new List<string>();
        foreach (var part in joined[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == ".." && segments.Count > 0 && segments[^1] != "..") { segments.RemoveAt(segments.Count - 1); continue; }
            if (part == ".." && root.Length > 0) continue;
            segments.Add(part);
        }
        var result = root + string.Join(Path.DirectorySeparatorChar, segments);
        if (joined.EndsWith(Path.DirectorySeparatorChar) && result.Length > 0 && !result.EndsWith(Path.DirectorySeparatorChar)) result += Path.DirectorySeparatorChar;
        return result.Length == 0 ? "." : result;
    }

    internal static bool Exists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static string Slug()
    {
        string[] adjectives = ["brave", "calm", "clever", "cosmic", "crisp", "curious", "eager", "gentle", "glowing", "happy", "hidden", "jolly", "kind", "lucky", "mighty", "misty", "neon", "nimble", "playful", "proud", "quick", "quiet", "shiny", "silent", "stellar", "sunny", "swift", "tidy", "witty"];
        string[] nouns = ["cabin", "cactus", "canyon", "circuit", "comet", "eagle", "engine", "falcon", "forest", "garden", "harbor", "island", "knight", "lagoon", "meadow", "moon", "mountain", "nebula", "orchid", "otter", "panda", "pixel", "planet", "river", "rocket", "sailor", "squid", "star", "tiger", "wizard", "wolf"];
        return adjectives[Random.Shared.Next(adjectives.Length)] + "-" + nouns[Random.Shared.Next(nouns.Length)];
    }
}
