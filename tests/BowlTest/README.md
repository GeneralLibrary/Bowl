# Bowl library tests

The 152 migrated xUnit tests cover the existing `GeneralUpdate.Bowl`
.NET Standard 2.0 diagnostic library. They run under .NET 10 and retain their
original source and attribution.

From the independent Bowl repository:

```powershell
dotnet test tests\BowlTest\BowlTest.csproj
dotnet test tests\BowlTest\BowlTest.csproj --filter "FullyQualifiedName~BowlCrashPipelineTests"
dotnet test tests\BowlTest\BowlTest.csproj --collect:"XPlat Code Coverage"
```

Coverage includes context/result types, platform strategies, process runner,
crash serialization/reporting, diagnostics callbacks and legacy backup copying.
Platform-specific cases retain their original platform checks; the test count
alone does not prove procdump runs on every platform.

The standalone host's protocol, process-survival and durable reporting tests are
in `tests\Bowl.Host.Tests`. Run `dotnet test Bowl.slnx` for both suites. A
diagnostic tool exiting without a dump is not application health verification.
