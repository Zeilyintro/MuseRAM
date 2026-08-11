param(
    [ValidateSet('Fast', 'Full')]
    [string]$Mode = 'Fast'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = [System.IO.Path]::GetDirectoryName($repositoryRoot)
$dotnet = Join-Path $workspaceRoot 'MuseRAM-DevTools\.dotnet\dotnet.exe'
$cliHome = Join-Path $workspaceRoot 'MuseRAM-DevTools\dotnet-home'
$solution = Join-Path $repositoryRoot 'MuseRAM.sln'

if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "MuseRAM .NET SDK was not found: $dotnet"
}
if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
    throw "MuseRAM solution was not found: $solution"
}

$env:DOTNET_CLI_HOME = $cliHome
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-MuseRamTests {
    param(
        [string]$Configuration,
        [string]$Filter
    )

    $arguments = @(
        'test',
        $solution,
        '--configuration', $Configuration,
        '--no-restore',
        '--verbosity', 'minimal'
    )
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $arguments += @('--filter', $Filter)
    }

    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "MuseRAM $Configuration tests failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    if ($Mode -eq 'Fast') {
        Invoke-MuseRamTests -Configuration 'Debug' -Filter 'Category!=SourceContract'
    }
    else {
        Invoke-MuseRamTests -Configuration 'Debug' -Filter ''
        Invoke-MuseRamTests -Configuration 'Release' -Filter ''
    }
}
finally {
    Pop-Location
}
