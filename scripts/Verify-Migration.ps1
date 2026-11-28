$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $modified = @(
        'src/GeneralUpdate.Bowl/GeneralUpdate.Bowl.csproj',
        'src/GeneralUpdate.Bowl/BowlBootstrap.cs',
        'src/GeneralUpdate.Bowl/BowlResult.cs',
        'src/GeneralUpdate.Bowl/BowlContext.cs',
        'src/GeneralUpdate.Bowl/Applications/Windows/export.bat',
        'src/GeneralUpdate.Bowl/Internal/WindowsSystemInfoProvider.cs',
        'src/GeneralUpdate.Bowl/Strategies/WindowsBowlStrategy.cs',
        'src/GeneralUpdate.Bowl/Strategies/ProcessRunner.cs',
        'src/GeneralUpdate.Bowl/Strategies/StrategyFactory.cs',
        'src/GeneralUpdate.Bowl/Strategies/ProcessRunner.cs',
        'src/GeneralUpdate.Bowl/Tracer/GeneralTracer.cs',
        'src/GeneralUpdate.Bowl/Tracer/TextTraceListener.cs',
        'tests/BowlTest/README.md',
        'tests/BowlTest/BowlContextTests.cs',
        'tests/BowlTest/Strategies/BowlContextTests.cs',
        'tests/BowlTest/Integration/BowlIntegrationTests.cs',
        'tests/BowlTest/BowlTests.cs',
        'tests/BowlTest/Integration/BowlCrashPipelineTests.cs',
        'tests/BowlTest/Strategies/ProcessRunnerTests.cs',
        'tests/BowlTest/Strategies/WindowsBowlStrategyTests.cs'
    )
    $manifest = @(Import-Csv 'docs\migration-manifest.csv')
    if ($manifest.Count -ne 61) { throw 'Expected 61 original migration entries.' }
    foreach ($entry in $manifest) {
        $path = $entry.path.Replace('/', '\')
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $entry.destinationSha256) {
            throw "Destination integrity mismatch: $path"
        }
        if ($modified -notcontains $entry.path -and $hash -ne $entry.sha256) {
            throw "Undocumented migration change: $path"
        }
    }
    Write-Output 'Verified all 61 migrated files; original binaries and images are byte-identical.'
}
finally {
    Pop-Location
}
