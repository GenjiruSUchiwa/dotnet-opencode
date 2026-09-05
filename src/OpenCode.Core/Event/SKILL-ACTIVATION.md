# Standalone skill activation publisher

`SessionSkillPublisher(IDatabase)` implements the existing `ISessionSkillPublisher`
contract with `SessionSkillJsonContext.Default.SessionSkillActivatedData`.

Server registration:

```csharp
services.AddSingleton<ISessionSkillPublisher, SessionSkillPublisher>();
services.AddSessionSkillServices();
```

Mount the existing standalone endpoint only with this real publisher registered.
The publisher appends `session.skill.activated.1` with exactly sessionID, skill id,
name and raw registered text. It uses the existing Session admission/removal
accounting and canonical EventStore transaction, checks current Session existence
and sequenced state, projects the message, reserves the sequence and notifies only
after commit. It does not execute a skill tool, admit a synthetic/user inbox item,
or schedule model work itself.

The projector uses the committed event ID (`evt_…` to `msg_…`), type `skill`, source
skill/name/text fields, event metadata when present and the event's created time.
It does not add the model SkillTool's sampled-files/base-directory wrapper. A
duplicate supplied event ID fails publication; it is not replay, first-wins inbox
reconciliation, an overwrite, or a successful no-op. Thus a failed duplicate cannot
trigger the service's post-commit resume.

The existing SessionSkillService owns catalog lookup, message-to-event ID mapping,
and forced asynchronous ResumeHostedAsync unless resume is false. The owned SDK
registers this same service/publisher and exposes ActivateSkillAsync. Shutdown joins
the service's owned resumes before closing the Session/event/Location dependencies.

Source: schema/session-event.ts Skill.Activated and
core/session/message-updater.ts lines 154–165; operation ordering is in core/session.ts.
No Server endpoint or skill producer was replaced by a fallback implementation.

SDK archive ImportSessionAsync/ExportSessionAsync remain connected to the real
archive adapter. Normal/title/Generate headers use SessionRequestIdentity from
actual SdkHostOptions and the generated build fingerprint; no product version or
application identity is invented by this publisher.

Verification: isolated repository .NET 11 SDK build passed with zero warnings/errors.
No tests, activation, skill loading, SDK construction, import/export, database,
filesystem, models, processes, APIs or network operations were executed for verification.
