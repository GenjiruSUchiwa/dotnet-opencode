# Captured Code Mode integration

The Session engine binds each permission-filtered ToolSnapshot with the real
`JintCodeModeEvaluator` and explicit `CodeModeLimits`, both for explicit readiness
inspection and for each physical attempt. MCP observation/reload precedes snapshot
capture. The request advertises and invokes `execute` from that same snapshot.
Nested search remains attached to its original capture. There is no provider loop,
second tool registry, or independent permission service.

The old nonempty-catalog rejection now applies only when `CodeModeExecutable` is
false. Binding does not change ToolSnapshot's execute-permission suppression. A
suppressed catalog produces a Removed source; an enabled empty catalog produces
the source's explicit no-tools instructions.

`CodeModeInstructionSource` consumes `CodeModeDiscovery`, never flattened direct
tool names. It stores the source Summary shape (`total`, `shown`, `namespaces` with
name/count/path/line entries), including pinned listings and the 2,000-token
round-robin listing budget. Initial/current hashes and changes use the existing
`session.instructions.updated.2` pipeline. Stored baselines render without access
to the live catalog, including after execute permission is removed. Chronological
changes use the source full-replacement wording; compact delta optimization is not
implemented. No new event or mutable catalog object is persisted.

## Additive Database seam

The optional observation now forwards through `SessionStore.SelectInstructionsAsync`:

```csharp
InstructionSource? mcp = null, InstructionSource? codeMode = null
// ...
LocalInstructions.ReadAsync(..., projectDirectory, mcp, codeMode)
```

The Session calls, Database forwarding seam and LocalInstructions parameter are
implemented together. Existing callers that omit the new argument remain compatible.

## Explicit native host budgets

The engine/SDK accept `codeModeLimits:`. The engine's default policy is:

| Budget | Native value |
| --- | ---: |
| Timeout | 60,000 ms |
| Nested tool calls | 64 |
| Output | 1 MiB |
| Detected owner-thread allocation | 64 MiB |
| Recursion depth | 64 |
| Source | 262,144 bytes |
| Boundary JSON | 4,194,304 bytes |
| Execution checks | 100,000 |
| Syntax nodes | 20,000 |

These are explicit finite native host choices, **not upstream's unlimited runtime
defaults**. They can interrupt long permission waits/tool programs. Allocation
tracking is not a hard process heap limit, and the evaluator is not OS isolation.
The Jint adapter's documented restricted JavaScript/standard-library subset and
diagnostics remain authoritative; this integration does not claim complete source
language parity or adversarial sandbox verification.

Sources: `core/tool.ts`, `core/codemode/catalog.ts`, `core/codemode/instructions.ts`,
and `core/codemode/tool.ts`. See `../CodeMode/README.md` for evaluator limits.
Verification is compilation only; no JavaScript, MCP, tools, providers or runtime
database operations were run.
