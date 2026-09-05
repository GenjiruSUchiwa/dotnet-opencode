namespace OpenTui.Blazor.Code;

using System.Security.Cryptography;
using System.Text;

internal static class TreeSitterAssets
{
    private static readonly IReadOnlyDictionary<string, string> Hashes = new Dictionary<string, string>
    {
        ["tree-sitter.wasm"] = "f38dcc4b43b818f9a0785bc1c6d5611a75ac4cdd428ff3f02757c34ca4e46d7f",
        ["javascript.tree-sitter-javascript.wasm"] = "5fb488d0cabb4775a594bab85682de5ad6ce83c0d6ac997a9f82dd084d571240",
        ["typescript.tree-sitter-typescript.wasm"] = "778025db5a8be0e70f8ccc3671e486dfeddd048c25d9e8a70c26de2e1bf6f97d",
        ["markdown.tree-sitter-markdown.wasm"] = "3e13182f21373634c40653f170e6f2d2790914eb2c243927086d79023c534f7a",
        ["markdown_inline.tree-sitter-markdown_inline.wasm"] = "9bbd71a70a23f6d0193bb162a72eecf2a2bd9ce76910d7ad3da9c5f80f122671",
        ["zig.tree-sitter-zig.wasm"] = "54b3b83dd9c62da5815f06132bc3fc914d9dcc780370b32416446a0b7969e8c6",
        ["javascript.highlights.scm"] = "c90e849891a3c8698992e10efdeaff7e7a8f98da47f941489566cb9e60f639b5",
        ["typescript.highlights.scm"] = "8e823819058d480c450ca4ef377a05675f6ed6f7c0460a69b972cdbe940c0893",
        ["markdown.highlights.scm"] = "f3b02df1a9213cfecfb6936bce8db2f777edd523fc23ed890695e6cb4552d556",
        ["markdown.injections.scm"] = "a2bf8c052454acbe765970a4ad2706c60cad7b62a762b927e28f60af1a0ef516",
        ["markdown_inline.highlights.scm"] = "ca9a109ddd21c5ffdc6b84f00c0a4b2eeb55ae36a0b8eeb9b02348269c5acd22",
        ["zig.highlights.scm"] = "f2232f0fde717543e4541cec871dd8633cb9bc6b5d1686a56b2d469c1dd9e6b6",
    };

    internal static string? Language(string name) => name switch
    {
        "javascript" or "javascriptreact" or "js" or "jsx" => "javascript",
        "typescript" or "typescriptreact" or "ts" or "tsx" => "typescript",
        "markdown" or "md" => "markdown",
        "markdown_inline" => "markdown_inline",
        "zig" => "zig",
        _ => null,
    };

    internal static byte[] Read(string name)
    {
        using var source = typeof(TreeSitterAssets).Assembly.GetManifestResourceStream("OpenTui.Blazor.Code.Assets." + name)
            ?? throw new InvalidOperationException($"Missing bundled Tree-sitter asset: {name}");
        using var output = new MemoryStream();
        source.CopyTo(output);
        var bytes = output.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Hashes[name], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Tree-sitter asset hash mismatch: {name}");
        return bytes;
    }

    internal static string Query(string language, string kind) => Encoding.UTF8.GetString(Read($"{language}.{kind}.scm"));
}
