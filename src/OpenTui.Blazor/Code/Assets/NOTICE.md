# Parser assets and third-party attribution

These grammar/query files are unchanged copies of the installed `@opentui/core`
0.5.9 assets. Their SHA-256 pins are in `../TreeSitterAssets.cs` and the original
`../PARSER-PROPOSAL.md`. They are embedded, not downloaded by the application.

OpenTUI source commit: `df2fc1594bb7a1274fc490155305e3d9f61f1b01`.
The parser descriptors at that commit identify the following grammar releases:

| Directory | Grammar source | Release | License |
| --- | --- | --- | --- |
| javascript | tree-sitter/tree-sitter-javascript | v0.25.0 | MIT |
| typescript | tree-sitter/tree-sitter-typescript | v0.23.2 | MIT |
| markdown, markdown_inline | tree-sitter-grammars/tree-sitter-markdown | v0.5.1 | MIT |
| zig | tree-sitter-grammars/tree-sitter-zig | v1.1.2 | MIT |

`LICENSE.grammars` retains their individual copyright notices and MIT terms.
The JavaScript query comes from tree-sitter-javascript. TypeScript (including
ECMA), Zig and Markdown injection queries come from nvim-treesitter; Markdown
and Markdown-inline highlights include OpenTUI's preserved modifications.
The files retain their existing source comments and are not rewritten here.

nvim-treesitter queries are under Apache-2.0; the license text is included as
`LICENSE.apache-2.0`, inspected at the repository's v0.10.0 tag. OpenTUI's source
descriptors used floating master query URLs. This port pins the actual shipped
query bytes and OpenTUI commit, not a guessed nvim-treesitter revision. This is
not a claim that the query files are from that license-reference tag.

`tree-sitter.wasm` is from web-tree-sitter 0.25.10. `LICENSE.web-tree-sitter`
retains its MIT license, copyright 2018–2024 Max Brunsfeld. The C# host/query code
ports the web binding and OpenTUI worker behavior; it does not execute their JS.
OpenTUI's MIT license is included in `../LICENSE.opentui`.

Wasmtime NuGet `[44.0.0]`:

- Source repository: https://github.com/bytecodealliance/wasmtime-dotnet
- Source commit from restored package metadata:
  `c1b7f660ce490f7acf757b558235b0c916e802e0`
- Package SHA-512 (base64, restored NuGet metadata):
  `lVYhl1q6EEyHbPMi9UslMpxVR67ijvpJPKdZsvcUq18CgWrf2q4yebNwmODtkDV0HnKV34UOiyeUoNBue//GiA==`
- License: Apache-2.0 WITH LLVM-exception, included in `LICENSE.apache-2.0`
  and `LICENSE.wasmtime-exception`.

The build copies these notices/licenses to output/publish directories. Binary
redistribution must retain them and the Wasmtime distribution's applicable
third-party notices. No Wasmtime/native/WASM execution was used to inspect assets.
