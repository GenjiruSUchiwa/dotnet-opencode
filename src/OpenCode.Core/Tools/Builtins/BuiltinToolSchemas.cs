namespace OpenCode.Core.Tools.Builtins;

using System.Text.Json;

internal static class BuiltinToolSchemas
{
    public static JsonElement WebFetch { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"url":{"type":"string"},"contentType":{"type":"string"},
          "format":{"enum":["text","markdown","html"]},"output":{"type":"string"}},"required":["url","contentType","format","output"]}
        """);
    public static JsonElement Skill { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"name":{"type":"string"},"directory":{"type":"string"},"output":{"type":"string"}},
         "required":["name","directory","output"]}
        """);
    public static JsonElement Read { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"oneOf":[
          {"type":"object","properties":{"type":{"const":"file"},"uri":{"type":"string"},"name":{"type":"string"},
            "content":{"type":"string"},"encoding":{"enum":["utf8","base64"]},"mime":{"type":"string"}},
            "required":["type","uri","name","content","encoding","mime"]},
          {"type":"object","properties":{"type":{"const":"text-page"},"content":{"type":"string"},"mime":{"type":"string"},
            "offset":{"type":"integer","minimum":1},"truncated":{"type":"boolean"},"next":{"type":"integer","minimum":1}},
            "required":["type","content","mime","offset","truncated"]},
          {"type":"object","properties":{"type":{"const":"list-page"},"entries":{"type":"array","items":{"type":"object",
            "properties":{"path":{"type":"string"},"type":{"enum":["file","directory","symlink"]}},"required":["path","type"]}},
            "truncated":{"type":"boolean"},"next":{"type":"integer","minimum":1}},"required":["type","entries","truncated"]}
        ]}
        """);
    private static readonly JsonElement Entry = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"path":{"type":"string"},"type":{"const":"file"}},"required":["path","type"]}
        """);
    private static readonly JsonElement FileDiff = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{
          "file":{"type":"string"},"patch":{"type":"string"},
          "additions":{"type":"integer","minimum":0},"deletions":{"type":"integer","minimum":0},
          "status":{"enum":["added","deleted","modified"]}},
         "required":["file","patch","additions","deletions","status"]}
        """);

    public static JsonElement Edit { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { files = new { type = "array", items = FileDiff }, replacements = new { type = "integer", minimum = 0 } },
        required = new[] { "files", "replacements" }
    });
    public static JsonElement Write { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"operation":{"const":"write"},"target":{"type":"string"},
          "resource":{"type":"string"},"existed":{"type":"boolean"}},"required":["operation","target","resource","existed"]}
        """);
    public static JsonElement Glob { get; } = JsonSerializer.SerializeToElement(new { type = "array", items = Entry });
    public static JsonElement Grep { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "array",
        items = new
        {
            type = "object",
            properties = new
            {
                entry = Entry,
                line = new { type = "integer", minimum = 1 },
                offset = new { type = "integer", minimum = 0 },
                text = new { type = "string" },
                submatches = JsonSerializer.Deserialize<JsonElement>("""
                    {"type":"array","items":{"type":"object","properties":{"text":{"type":"string"},
                      "start":{"type":"integer","minimum":0},"end":{"type":"integer","minimum":0}},"required":["text","start","end"]}}
                    """)
            },
            required = new[] { "entry", "line", "offset", "text", "submatches" }
        }
    });
    public static JsonElement Shell { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"exit":{"type":"integer"},"shellID":{"type":"string"},
          "truncated":{"type":"boolean"},"timeout":{"type":"boolean"},"output":{"type":"string"},
          "status":{"enum":["completed","running"]}},"required":["output","truncated"]}
        """);
}
