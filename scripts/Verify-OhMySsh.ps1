[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$verificationArtifactsPath = Join-Path $workspacePath '.local\verification-build'
$verificationAssemblyPath = Join-Path $verificationArtifactsPath 'bin\OhMySsh.Verify\debug\OhMySsh.Verify.dll'
$env:DOTNET_CLI_HOME = Join-Path $workspacePath '.local\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:MSBuildEnableWorkloadResolver = 'false'

Push-Location $workspacePath
try {
    # Keep verification builds separate from the application's normal output and any running instance.
    dotnet build tools/OhMySsh.Verify/OhMySsh.Verify.csproj --artifacts-path $verificationArtifactsPath --verbosity minimal -m:1 -nr:false /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not build the verifier.'
    }

    dotnet $verificationAssemblyPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Verification failed. See the generated report.'
    }
}
finally {
    Pop-Location
}
