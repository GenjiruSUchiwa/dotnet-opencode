# Timestamped tool releases

- Repository: private [`Hona/dotnet-opencode`](https://github.com/Hona/dotnet-opencode).
- Package: public `dotnet-opencode` on NuGet.org.
- Installed command: **`dotnet opencode`**.
- Workflow: [`.github/workflows/publish.yml`](../.github/workflows/publish.yml).
- Each default-branch push builds a development prerelease. Other branches and tags cannot enter the publishing job.
- Version: `0.1.0-ci.<UTC commit timestamp>.<GitHub run ID>.<run attempt>`.
- A matching GitHub prerelease retains the package and source commit.
- Packaging must succeed before any publishing credential is requested. The workflow does not launch the application or perform native/runtime verification.
- Gitleaks is available for optional manual scans through `.github/scripts/scan-secrets.ps1`; it is not a publishing gate. Run it from the repository root with complete Git history available. Its binary is version/hash pinned and reports redact secret values. The only project allowance is an exact upstream migration ID in its declaration file.

## NuGet.org trusted publisher

| Setting | Value |
| --- | --- |
| Policy name | `dotnet-opencode` |
| Package owner | `Hona` |
| CI/CD provider | GitHub Actions |
| Repository owner | `Hona` |
| Repository | `dotnet-opencode` |
| Workflow file | `publish.yml` — filename only |
| Environment | `nuget` |
| Push scope | Push new packages and package versions |
| Package pattern | `dotnet-opencode` — no wildcard |
| Unlist/relist | Disabled |

## GitHub configuration

- Repository variable **`NUGET_USER=Hona`** contains the NuGet profile name, not an email address.
- Environment **`nuget`** permits deployment from **`main`**. Update that branch policy if the repository's default branch changes.
- The job receives `id-token: write` for OIDC and `contents: write` for the matching GitHub prerelease.
- **No long-lived `NUGET_API_KEY` secret is stored.** `NuGet/login` exchanges GitHub's OIDC token for a temporary publishing key immediately before the push.
- `GITHUB_TOKEN` is supplied automatically by GitHub Actions; no personal access token is required.
- Action implementations are pinned to inspected commit SHAs.

Private-repository policies can start with a seven-day temporary activation window. Complete the first successful publication within that window, or restart the window in NuGet.org. See [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Install a published snapshot

```powershell
dotnet tool install --global dotnet-opencode --prerelease
dotnet opencode
```

To update, use `dotnet tool update --global dotnet-opencode --prerelease`.
To pin a snapshot, replace `--prerelease` with `--version <published-version>`.

The .NET tool shim is named `dotnet-opencode`; the .NET CLI resolves that prefix when invoked as `dotnet opencode`. The local checkout name and existing `dotnet`-channel data filenames do not need to change.

## First successful publication

- [Workflow run 33938379734](https://github.com/Hona/dotnet-opencode/actions/runs/33938379734) completed successfully on September 5, 2026.
- Package version: `0.1.0-ci.20260905021128.33938379734.1`, source commit `6e28d830d2d4b23f248348bc234b8140bfd682c2`.
- The clean GitHub runner installed the exact pinned SDK, built/packed the tool, and passed static package inspection.
- `NuGet/login` authenticated with GitHub OIDC. NuGet.org accepted the package upload, and the workflow created the [matching GitHub prerelease](https://github.com/Hona/dotnet-opencode/releases/tag/v0.1.0-ci.20260905021128.33938379734.1).
- NuGet indexing can lag a successful upload. Publication does not establish tool installation, application startup, database or native-runtime compatibility; none of those were executed for verification.
- The public [NuGet download index](https://api.nuget.org/v3-flatcontainer/dotnet-opencode/index.json) subsequently listed the first version, confirming that indexing completed. [Run 33938749468](https://github.com/Hona/dotnet-opencode/actions/runs/33938749468) also published successfully from the next `main` push.
