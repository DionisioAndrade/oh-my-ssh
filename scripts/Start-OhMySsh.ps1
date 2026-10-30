[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

function Request-OhMySshActivation {
    param(
        [string] $ActivationEventName,
        [string] $InstanceMutexName,
        [ValidateRange(1, 600000)] [int] $StartupTimeoutMilliseconds = 5000,
        [ValidateRange(1, 1000)] [int] $PollMilliseconds = 100
    )

    $activationTimer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $activationHandle = $null
        try {
            $activationHandle = [System.Threading.EventWaitHandle]::OpenExisting($ActivationEventName)
        }
        catch [System.Threading.WaitHandleCannotBeOpenedException] {
            # The instance may still be creating its activation event, or may have closed it.
        }
        if ($null -ne $activationHandle) {
            try { $activationHandle.Set() | Out-Null }
            finally { $activationHandle.Dispose() }
            return $true
        }

        $instanceHandle = $null
        try {
            $instanceHandle = [System.Threading.Mutex]::OpenExisting($InstanceMutexName)
        }
        catch [System.Threading.WaitHandleCannotBeOpenedException] {
            return $false
        }
        finally {
            # Holding this handle after app exit would interfere with App's createdNew check.
            if ($null -ne $instanceHandle) { $instanceHandle.Dispose() }
        }
        if ($activationTimer.ElapsedMilliseconds -ge $StartupTimeoutMilliseconds) {
            throw 'oh-my-ssh is starting or closing and cannot receive activation yet. Retry shortly; no build was started.'
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
}

function Start-OhMySshSession {
    param(
        [Parameter(Mandatory = $true)] [scriptblock] $Build,
        [Parameter(Mandatory = $true)] [scriptblock] $Launch,
        [string] $ActivationEventName = 'Local\OhMySsh.Desktop.Activate',
        [string] $InstanceMutexName = 'Local\OhMySsh.Desktop.Instance',
        [string] $LauncherMutexName = 'Local\OhMySsh.Desktop.Launcher',
        [ValidateRange(1, 600000)] [int] $StartupTimeoutMilliseconds = 5000,
        [ValidateRange(1, 600000)] [int] $LauncherTimeoutMilliseconds = 60000,
        [ValidateRange(1, 1000)] [int] $PollMilliseconds = 100
    )

    $activationArguments = @{
        ActivationEventName = $ActivationEventName
        InstanceMutexName = $InstanceMutexName
        StartupTimeoutMilliseconds = $StartupTimeoutMilliseconds
        PollMilliseconds = $PollMilliseconds
    }
    $activatedMessage = 'oh-my-ssh is already running. Its window was restored. Close the app first if you want to rebuild and load source changes.'
    if (Request-OhMySshActivation @activationArguments) {
        Write-Output $activatedMessage
        return
    }

    # Serialize launchers as well as app instances: a second script must not rebuild
    # while the first is handing off its freshly built executable to Windows.
    $launcherMutex = [System.Threading.Mutex]::new($false, $LauncherMutexName)
    $ownsLauncherMutex = $false
    $applicationProcess = $null
    try {
        $launcherTimer = [System.Diagnostics.Stopwatch]::StartNew()
        while (-not $ownsLauncherMutex) {
            try { $ownsLauncherMutex = $launcherMutex.WaitOne(0) }
            catch [System.Threading.AbandonedMutexException] { $ownsLauncherMutex = $true }
            if ($ownsLauncherMutex) { break }
            if (Request-OhMySshActivation @activationArguments) {
                Write-Output $activatedMessage
                return
            }
            if ($launcherTimer.ElapsedMilliseconds -ge $LauncherTimeoutMilliseconds) {
                throw 'Another launcher is still preparing oh-my-ssh. Retry after it finishes.'
            }
            Start-Sleep -Milliseconds $PollMilliseconds
        }

        if (Request-OhMySshActivation @activationArguments) {
            Write-Output $activatedMessage
            return
        }
        & $Build
        if (Request-OhMySshActivation @activationArguments) {
            Write-Output $activatedMessage
            return
        }
        $applicationProcess = & $Launch
        $startupTimer = [System.Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            if (Request-OhMySshActivation @activationArguments) {
                Write-Output 'The oh-my-ssh window is ready.'
                return
            }
            if ($applicationProcess -is [System.Diagnostics.Process] -and $applicationProcess.HasExited) {
                throw 'oh-my-ssh exited before its window became ready. Check the application diagnostics.'
            }
            if ($startupTimer.ElapsedMilliseconds -ge $StartupTimeoutMilliseconds) {
                throw 'oh-my-ssh has not confirmed startup yet. Retry to restore its window; no process was stopped.'
            }
            Start-Sleep -Milliseconds $PollMilliseconds
        }
    }
    finally {
        if ($applicationProcess -is [System.Diagnostics.Process]) { $applicationProcess.Dispose() }
        if ($ownsLauncherMutex) { $launcherMutex.ReleaseMutex() }
        $launcherMutex.Dispose()
    }
}

$workspacePath = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspacePath '.local\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:MSBuildEnableWorkloadResolver = 'false'

Push-Location $workspacePath
try {
    Start-OhMySshSession -Build {
        dotnet build OhMySsh.sln --configuration $Configuration --verbosity minimal -m:1 -nr:false /p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) {
            throw 'The build did not complete. See the errors above.'
        }
    } -Launch {
        $executablePath = Join-Path $workspacePath "src\OhMySsh.Desktop\bin\$Configuration\net10.0-windows\OhMySsh.exe"
        Start-Process -FilePath $executablePath -WorkingDirectory $workspacePath -WindowStyle Normal -PassThru
    }
}
finally {
    Pop-Location
}
