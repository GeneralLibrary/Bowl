# Bowl

Bowl is the independently maintained update guardian from
[GeneralUpdate](https://github.com/GeneralLibrary/GeneralUpdate).
It runs **on demand as a separate process**, not as a required system service.

This repository contains two independently usable components:

| Component | Target | Purpose |
| --- | --- | --- |
| `src\Bowl.Host` | .NET 8 | Versioned disk protocol, updater/application supervision, optional rollback and persistent HTTP outbox |
| `src\GeneralUpdate.Bowl` | .NET Standard 2.0 | Existing diagnostic library, platform strategies, procdump integration and `BowlContext.OnCrash` callback |

The host has no GeneralUpdate project/source dependency and does not depend on
procdump. It records metadata by default and never collects or uploads dumps.
The library remains available for applications explicitly opting into richer
diagnostics; its tool-success result does **not** establish application health.

## Build, test and deploy

Use the .NET 10 SDK for the solution/test projects; the framework-dependent host
requires the .NET 8 runtime. From this repository:

```powershell
dotnet build Bowl.slnx -c Release
dotnet test Bowl.slnx -c Release
dotnet publish src\Bowl.Host\Bowl.Host.csproj -c Release -r win-x64 --self-contained true -o artifacts\host\win-x64
dotnet pack src\GeneralUpdate.Bowl\GeneralUpdate.Bowl.csproj -c Release -o artifacts\packages
powershell -File scripts\Verify-Migration.ps1
```

For Linux use `linux-x64` as the publish RID; platform-specific diagnostic tools
in the legacy library require their own installation. Windows is the locally
verified host platform. CI also runs the host tests on Linux.

Deploy the **whole publish directory** to a trusted location outside the
application, updater, package staging, backup and state directories. Configure
GeneralUpdate to start `Bowl.Host.exe` directly, not a shell wrapper or launcher.
The host never starts the application; the updater publishes the exact process
identity after doing so.

GeneralUpdate's `Monitoring.Enabled` is opt-in. Set `ExecutablePath` to the host,
`DiagnosticsDirectory` to a durable protected state root, and explicitly choose
`AutoRollback` and `VerifyLaunch`. If enabled monitoring cannot become ready,
the updater aborts before file modification. No dump is required to detect an
abnormally terminated updater or failed application startup.

See [the complete protocol and operations guide](docs/protocol-v1.md).

## Recovery and retry

Normal startup, and explicit recovery of the **same** interrupted attempt:

```powershell
.\Bowl.Host.exe --attempt 4b0a8245-204d-464a-b86b-65c8f4519a8e --state-root C:\ProgramData\Example\UpdateState
```

Bounded delivery/deferred-rollback sweep:

```powershell
.\Bowl.Host.exe --retry 100 --state-root C:\ProgramData\Example\UpdateState
```

Each eligible attempt gets one HTTP attempt per invocation, with a persisted
5-second-to-1-hour exponential backoff. Have your existing application/operations
scheduler invoke the sweep, and resume incomplete observations by attempt ID.
**No scheduler/service is silently installed, and retry is not guaranteed while
no host or scheduler is running.** A host crash before `result.json` requires
explicit `--attempt` recovery. A rollback deferred because the updater remains
alive is not uploaded as completed recovery.

All attempts for one installation must use the **same state root and operating
identity**. Independent roots, multiple writers, untrusted local accounts or
network filesystems are not coordinated by this protocol. Use local storage
with atomic replace semantics and directory ACLs restricted to the updater,
host and operator. Do not let application update packages write into it.

## Migration and attribution

The original Apache-2.0 license and JusterZhu authorship are retained. See
[migration provenance and integrity](docs/migration.md),
[the 61-file manifest](docs/migration-manifest.csv), and
[third-party diagnostic tools](THIRD-PARTY-NOTICES.md).
The migration does not relicense Microsoft binaries.

No source deletion, pull request or remote push is performed by this repository's
migration. Removing the old component from GeneralUpdate is a separate
coordinated change after the migration and integration evidence is accepted.
