namespace OpenCode.Cli.Commands.Run;

public sealed record RunOptions(string[] Message, string[] Files, bool Continue = false, string? Session = null,
    bool Fork = false, string? Model = null, string? Agent = null, string Format = "default", string? Title = null,
    bool Thinking = false, bool Auto = false, string? Server = null, bool Standalone = false);
