$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $modified = @(
        'src/GeneralUpdate.Bowl/GeneralUpdate.Bowl.csproj',
        'src/GeneralUpdate.Bowl/BowlBootstrap.cs',
        'src/GeneralUpdate.Bowl/BowlResult.cs',
        'tests/BowlTest/README.md'
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
