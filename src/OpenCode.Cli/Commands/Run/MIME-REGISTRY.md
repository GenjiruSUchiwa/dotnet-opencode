# Source MIME registry

RunMimeRegistry.Generated.cs contains the complete extension lookup resolved from
the installed source dependencies, not a hand-picked attachment MIME list:

- mime-types **3.0.2**, using index.js `_preferredType` and mimeScore.js.
- mime-db **1.54.0**, resolved through that mime-types installation.
- **1,239** resulting extension keys. Ties choose the later source registry entry,
  exactly as the source score comparison does.

Source hashes and both upstream MIT notices are retained in the generated C# file.
The importer pins package versions and SHA-256 of index.js, mimeScore.js and db.json
so a changed lookup policy cannot silently reuse this generator without review.
It parses source JSON only; it never invokes Node/Bun, imports upstream modules,
loads native libraries, or executes the application.

Normal CLI builds compile the generated C# file through the existing default
Compile glob. No csproj edit, resource linker, package install, upstream path,
build import or runtime dependency on the source checkout is required. The generated
file is a source artifact, not a transient runtime cache; leave it with the changes.

## Explicit regeneration

Use the pinned .NET SDK full CLI build, adding these MSBuild properties:

```
OpenApiGenerateDocuments=false
CustomBeforeMicrosoftCommonTargets=<repo>/src/OpenCode.Cli/Commands/Run/Build/ImportMimeRegistry.targets
RunMimeSourceRoot=<source-worktree>/node_modules/.bun/mime-types@3.0.2/node_modules
```

The source root must contain that installation's mime-types and mime-db directories.
The target runs only for OpenCode.Cli, before PrepareForBuild, and writes only
Commands/Run/RunMimeRegistry.Generated.cs. This is a source-asset transformation
within the allowed build, not a MIME runtime test or filesystem probe. Default
builds do not run it. Do not hand-edit the generated map to patch a MIME conflict.

Lookup ports extname('x.' + path).toLowerCase() for Run's absolute file paths,
including dotfile and trailing-dot handling. The format registry resolves common
conflicts by facet/source/top-level-type/length score, not the obsolete legacy
nginx/apache/iana precedence alone. Missing extensions return
application/octet-stream, the actual FSUtil.mimeType fallback.

The existing 10 MiB/100-file checks and binary/UTF-8 roundtrip classification remain.
Image and PDF registry types are always attachments; other nonbinary UTF-8 input
is converted to source text/plain prompt markup. Known binary types retain their
registry MIME and bytes rather than being rejected or guessed as plain text.

Only builds/source reads were performed. No attachment was read through RunFiles,
no runtime MIME lookup was executed for verification, and no tests were run.
