# Attribution and third-party tools

The migrated GeneralUpdate.Bowl library and tests originate from
GeneralLibrary/GeneralUpdate commit
`126e5d830dc1f61144b3ac3a49b96b19ad4e37a3`.
Original author/package attribution: **JusterZhu**, copyright 2020-2026.
The original Apache License 2.0 text is preserved in `LICENSE`.

The original repository also tracked Microsoft diagnostic binaries:

- Windows Sysinternals ProcDump: `procdump.exe`, `procdump64.exe`,
  `procdump64a.exe`.
- Linux ProcDump 3.3.0: the `.deb` and two `.rpm` packages under
  `src\GeneralUpdate.Bowl\Applications\Linux`.

These six binaries are copied byte-for-byte, not rebuilt or relicensed under
Apache-2.0. Their publisher's license/EULA and any package-contained notices
continue to apply. The migration baseline did not include a separate Windows
ProcDump EULA file; do not infer a redistribution grant from the repository
license. Review Microsoft's applicable terms before redistributing these tools.
The new standalone host does not ship, start or auto-accept the EULA for them.

Images, tool-installation scripts, tests and metadata retain provenance in
`docs\migration-manifest.csv`. NuGet dependencies retain their own licenses.
