# Command Host

Register `CommandHostService` after the shared tool Location composition is
available. It borrows the existing `ToolLocationFactory` and `PermissionLocationMap`;
it must not construct a separate MCP runtime or permission service.

`AcquireAsync` retains a tool Location lease and observes that Location's actual
MCP runtime using `InstructionCatalog.ReadMcpConfiguration`, the same ordered
configuration composition used by execution. Core's `CommandRuntime.Create`
composes embedded init/review callbacks, MCP prompts, then local JSON/Markdown
commands. List and preparation use the same captured runtime and lease.

`ExecuteAsync` resolves the Session, prepares the command through Core callbacks,
and uses `PreparedCommand.AdmitAsync` with `SessionExecutionEngine.AdmitPromptAsync`
to prepare attachments, durably enqueue, and wake the host-owned coordinator.
Model/instruction readiness is execution-owned, not an HTTP pre-admission check.
Command-selected agents/models use the canonical mutation APIs. An unspecified
command agent does not reset the current Session model.

Without `IPermissionAwareCommandShell`, Core's template parser preflights local
commands, including shell expressions introduced by arguments, before selection
side effects. Embedded shell fails explicitly; it is not executed with a process
shortcut. The managed host now registers `PermissionAwareCommandShell`, which uses
`LocalShellPolicy`, source-less Session/Agent permission requests, and the existing
owned `ShellProcessSource`. It returns full combined UTF-8 capture bytes instead
of a truncated/no-output tool preview. Builtin and MCP prompt text is not shell-interpolated.

URI file, agent, and skill attachments now pass unchanged to the real Session
prompt preparation adapter. Its unsupported plugin/revert/media/lowering cases
remain explicit. Saved approvals use the shared durable store, not fabricated grants.

Daemon DI additions:

```csharp
builder.Services.AddSingleton<CommandHostService>();
builder.Services.AddSingleton<IPermissionAwareCommandShell, PermissionAwareCommandShell>();
builder.Services.AddSingleton<OpenCode.Core.Session.Transfer.SessionTransfer>();
```

SessionMovement remains the same singleton injected into SessionExecutionEngine
and SessionExecutionService. Move requests use its admission/wake API and retain
the host lifetime through coordinator settlement. Forks do not require idle or a
removal reservation and do not start execution.

Verification is compilation only. No commands, shell, provider, MCP connection,
database, or live configuration was executed for verification.
