[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspacePath '.local\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:MSBuildEnableWorkloadResolver = 'false'

Push-Location $workspacePath
try {
    dotnet build tools/OhMySsh.Verify/OhMySsh.Verify.csproj --verbosity minimal -m:1 -nr:false /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not build the verifier.'
    }

    dotnet tools/OhMySsh.Verify/bin/Debug/net10.0-windows/OhMySsh.Verify.dll
    if ($LASTEXITCODE -ne 0) {
        throw 'Verification failed. See the generated report.'
    }
}
finally {
    Pop-Location
}
