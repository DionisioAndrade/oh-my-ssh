[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspacePath '.local\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'

Push-Location $workspacePath
try {
    dotnet build OhMySsh.sln --configuration $Configuration --verbosity minimal -m:1 -nr:false /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw 'A compilação não foi concluída. Consulte os erros acima.'
    }

    $executablePath = Join-Path $workspacePath "src\OhMySsh.Desktop\bin\$Configuration\net10.0-windows\OhMySsh.exe"
    & $executablePath
}
finally {
    Pop-Location
}
