[CmdletBinding()]
param(
    [string] $PythonPath = '',
    [switch] $InstallFixtureDependencies
)

$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspacePath '.local\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:TEMP = Join-Path $workspacePath '.local\python-tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $PythonPath = Join-Path $workspacePath '.local\ssh-integration-venv\Scripts\python.exe'
}

Push-Location $workspacePath
try {
    if ($InstallFixtureDependencies) {
        if (-not (Test-Path -LiteralPath $PythonPath)) {
            python -m venv .local\ssh-integration-venv
            if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated Python fixture environment.' }
        }
        & $PythonPath -m pip install --disable-pip-version-check -r tools/OhMySsh.SshVerify/requirements.txt
        if ($LASTEXITCODE -ne 0) { throw 'Could not install the test-only SSH fixture dependencies.' }
    }
    if (-not (Test-Path -LiteralPath $PythonPath)) {
        throw 'The SSH fixture requires Python 3.10+ and AsyncSSH. Run this script with -InstallFixtureDependencies first, or provide -PythonPath.'
    }
    & $PythonPath -c 'import asyncssh, bcrypt'
    if ($LASTEXITCODE -ne 0) { throw 'The selected Python environment is missing the SSH fixture dependencies.' }
    dotnet build tools/OhMySsh.SshVerify/OhMySsh.SshVerify.csproj --verbosity minimal -m:1 -nr:false /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the SSH integration verifier.' }
    dotnet tools/OhMySsh.SshVerify/bin/Debug/net10.0-windows/OhMySsh.SshVerify.dll $PythonPath
    if ($LASTEXITCODE -ne 0) { throw 'SSH integration verification failed. See the generated report.' }
}
finally {
    Pop-Location
}
