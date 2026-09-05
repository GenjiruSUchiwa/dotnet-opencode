# dotnet-opencode third-party provenance

This development artifact is an independent .NET port, not an official OpenCode
release. The private project repository is Hona/dotnet-opencode; no NuGet publication
is performed or asserted by the pack task. The source checkout currently has no root project license;
this package does not invent one or substitute a third-party license for that
decision. Public redistribution requires the maintainer's project-license review.

- **OpenCode source/theme data**: MIT, copyright 2025 opencode. Theme provenance
  and its license are in third-party/theme-catalog-provenance.json.
- **OpenTUI native 0.5.9, win-x64**: SHA-256 equals the installed upstream npm
  artifact. third-party/opentui-0.5.9 includes its manifest, package metadata and
  license/notices for OpenTUI, Ghostty, LCMS2, libwebp, stb and Wuffs, plus libwebp
  authors and patent grant. No native module was loaded to establish identity.
- **Tree-sitter**: production content/queries and exact license hashes are under
  Tui/Transcript/GrammarAssets. The five built-in grammars/runtime remain embedded
  in OpenTui.Blazor.dll; their MIT/Apache/LLVM-exception notices are retained under
  third-party/tree-sitter-assets and Code/Assets. Excluded Clojure/Nix registrations
  are not packaged as usable parsers because source license provenance is incomplete.
- **MIME registry**: source hashes and MIME package MIT notices are retained in
  third-party/mime-registry-provenance.cs.txt.
- **fuzzysort-derived search**: third-party/LICENSE.fuzzysort.
- **NuGet dependencies**: third-party/nuget retains restored package metadata and
  supplied license/notice files. tool-runtime-assets.json records package versions,
  NuGet SHA-512 metadata and file hashes. A nuspec license expression/URL is not a
  fabricated local license text; some packages supply metadata rather than a file.

Platform runtime/shared-framework licenses remain with the installed .NET runtime.
This package does not bundle the .NET or ASP.NET Core shared frameworks.
