# Migration provenance and validation

Source: GeneralLibrary/GeneralUpdate, exact commit
`126e5d830dc1f61144b3ac3a49b96b19ad4e37a3`.
Target: GeneralLibrary/Bowl, independently maintained repository.

The target had no tracked project files or existing modifications. No existing
user content was overwritten, and the source worktree/main checkouts were not
modified. Raw `git cat-file blob` streams were used to preserve every byte,
including the source's mixed line endings.

## Inventory

`migration-manifest.csv` lists **61 tracked files**, with original Git blob,
size, SHA-256 and post-migration destination SHA-256:

- Entire `src\GeneralUpdate.Bowl` tree, including library source, all platform
  strategies, six procdump binaries, platform scripts and the package image.
- Entire `tests\BowlTest` tree.
- `imgs\bowl.jpeg`, `LICENSE`, `src\Directory.Build.props` and
  `tests\Directory.Packages.props`.

All 61 files were matched to their source Git blobs and SHA-256 before editing.
The six binaries and both image locations remain byte-identical. No binaries
were regenerated or downloaded from an unrelated source.

Intentional edits to imported files:

| File | Reason |
| --- | --- |
| `src\GeneralUpdate.Bowl\GeneralUpdate.Bowl.csproj` | Standalone repository URLs and explicit C# 12 for existing record syntax |
| `src\GeneralUpdate.Bowl\BowlBootstrap.cs` | Correct misleading no-dump health logging/documentation; behavior preserved |
| `src\GeneralUpdate.Bowl\BowlResult.cs` | Clarify legacy tool result is not application health |
| `tests\BowlTest\README.md` | Correct standalone test entry points and current coverage |

The manifest's `sha256` is always the original content. `destinationSha256`
captures the final imported files after those documented changes.
`scripts\Verify-Migration.ps1` verifies every destination and forbids undocumented
changes to the original import. It is an integrity snapshot, not a license or
authenticity assertion. When intentionally changing migrated files in future
maintenance, update the documented exceptions and destination snapshot.

## Independent implementation

`src\Bowl.Host` adds the executable .NET 8 guardian without cross-repository
references. The original .NET Standard library, callbacks, strategies and tests
remain compatible. `Bowl.slnx`, independent test dependency versions, host tests,
CI and root documentation provide standalone build/test/publish entry points.

The host's default metadata-only diagnostics intentionally do not run bundled
procdump or export system information. Optional legacy diagnostics remain an
explicit caller choice; no-dump results do not establish health.

## Validation and removal gate

The migrated library's 152 tests and the host's 49 tests passed in Release on
Windows (201 total). Host tests cover protocol
validation, exact process identities, full survival-window timing, early exit,
rollback fencing, interrupted recovery, nested backups, single-owner admission,
non-2xx persistence, backoff and acknowledgement/restart behavior.
Actual host termination/resumption, application/updater process exit, the 1 MiB
record bound, 10,000-attempt admission bound, and junction rejection are tested.
The library NuGet package and self-contained `win-x64` host publish both build.
The legacy library/test nullable/member-hiding warnings were retained rather
than changing unrelated migrated code; the new host treats warnings as errors.

GeneralUpdate integration commit
`348cf257a0ac5f4e5851d8d430f1da3c8a5e3548` adds opt-in tests using the actual
host executable through the real Core producer, not the old fake peer. The
integration owner reported 6/6 real-host scenarios and 1001/1001 Core tests:
files-only, process survival, process creation failure, early application exit,
actual updater termination, and HTTP 503 followed by a persistent 200 retry.
The final self-contained Release `artifacts\host\win-x64\Bowl.Host.exe` was
subsequently checked through those six real Core integration cases again:
6 passed, 0 failed, 0 skipped.
Set `GENERALUPDATE_REAL_BOWL_HOST` to this repository's published executable
when rerunning those tests in GeneralUpdate.

Source removal is a separate coordinated change, not performed here. Once the
final standalone test/publish/integrity checks and cross-repository integration
are accepted and the Bowl commit is retained, the old library/test directories,
their solution references and source-owned Bowl resources can be removed from
GeneralUpdate. Keep the new external producer/options/protocol/tests there.
No deployment should enable supervision before installing the real host and
arranging retries/recovery operations.
