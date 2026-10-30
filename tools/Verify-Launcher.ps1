[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$launcherPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\Start-OhMySsh.ps1'
$parseErrors = $null
$tokens = $null
$launcherAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $launcherPath, [ref] $tokens, [ref] $parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "The launcher has PowerShell parse errors: $($parseErrors[0].Message)"
}

# Load only function definitions. Never execute the real launcher's build or application entry point.
$functionNames = @('Request-OhMySshActivation', 'Start-OhMySshSession')
$definitions = foreach ($functionName in $functionNames) {
    $matchingFunctions = @($launcherAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
    }, $true))
    if ($matchingFunctions.Count -ne 1) {
        throw "Expected exactly one launcher function named '$functionName'."
    }
    $matchingFunctions[0].Extent.Text
}
$helperDefinitions = $definitions -join [Environment]::NewLine
. ([scriptblock]::Create($helperDefinitions))

function Assert-LauncherCondition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw $Message
    }
}

function New-LauncherTestNames {
    $prefix = 'Local\OhMySsh.LauncherVerification.' + [Guid]::NewGuid().ToString('N') + '.'
    [pscustomobject] @{
        ActivationEventName = $prefix + 'Activate'
        InstanceMutexName = $prefix + 'Instance'
        LauncherMutexName = $prefix + 'Launcher'
        ReadyEventName = $prefix + 'Ready'
    }
}

function New-LauncherTestState {
    param($Names)
    [hashtable]::Synchronized(@{
        ActivationEventName = $Names.ActivationEventName
        InstanceMutexName = $Names.InstanceMutexName
        LauncherMutexName = $Names.LauncherMutexName
        BuildCount = 0
        LaunchCount = 0
        InstanceMutex = $null
        ActivationEvent = $null
    })
}

function Close-OwnedTestMutex {
    param([System.Threading.Mutex] $Mutex)
    if ($null -ne $Mutex) {
        try {
            $Mutex.ReleaseMutex()
        }
        finally {
            $Mutex.Dispose()
        }
    }
}

function Close-LauncherTestState {
    param($State)
    if ($null -ne $State.ActivationEvent) {
        $State.ActivationEvent.Dispose()
    }
    Close-OwnedTestMutex $State.InstanceMutex
}

function Start-LauncherTestInvocation {
    param($Names, [string] $Invocation, $State)
    $ready = [System.Threading.EventWaitHandle]::new(
        $false, [System.Threading.EventResetMode]::ManualReset, $Names.ReadyEventName)
    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.Open()
    $powerShell = [powershell]::Create()
    $powerShell.Runspace = $runspace
    [void] $powerShell.AddScript({
        param($Definitions, $Invocation, $State, $ReadyName)
        $ErrorActionPreference = 'Stop'
        . ([scriptblock]::Create($Definitions))
        $ready = [System.Threading.EventWaitHandle]::OpenExisting($ReadyName)
        try {
            [void] $ready.Set()
            & ([scriptblock]::Create($Invocation)) $State
        }
        finally {
            $ready.Dispose()
        }
    }.ToString()).AddArgument($helperDefinitions).AddArgument($Invocation).
        AddArgument($State).AddArgument($Names.ReadyEventName)
    $async = $powerShell.BeginInvoke()
    $invocationState = [pscustomobject] @{
        PowerShell = $powerShell
        Runspace = $runspace
        Async = $async
        Ready = $ready
    }
    if (-not $ready.WaitOne(5000)) {
        Close-LauncherTestInvocation $invocationState
        throw 'The synthetic launcher invocation did not become ready.'
    }
    $invocationState
}

function Assert-LauncherTestPending {
    param($Invocation)
    Assert-LauncherCondition (-not $Invocation.Async.AsyncWaitHandle.WaitOne(150)) `
        'The launcher completed before the synthetic startup or launcher lock was ready.'
}

function Complete-LauncherTestInvocation {
    param($Invocation)
    Assert-LauncherCondition ($Invocation.Async.AsyncWaitHandle.WaitOne(5000)) `
        'The synthetic launcher invocation did not finish within five seconds.'
    $output = $Invocation.PowerShell.EndInvoke($Invocation.Async)
    if ($Invocation.PowerShell.Streams.Error.Count -gt 0) {
        throw $Invocation.PowerShell.Streams.Error[0]
    }
    $output
}

function Close-LauncherTestInvocation {
    param($Invocation)
    if ($null -ne $Invocation) {
        try {
            if (-not $Invocation.Async.IsCompleted) {
                $Invocation.PowerShell.Stop()
            }
        }
        finally {
            $Invocation.PowerShell.Dispose()
            $Invocation.Runspace.Close()
            $Invocation.Runspace.Dispose()
            $Invocation.Ready.Dispose()
        }
    }
}

$passed = 0
function Invoke-LauncherTest {
    param([string] $Name, [scriptblock] $Test)
    & $Test
    $script:passed++
    Write-Host "PASS: $Name"
}

Invoke-LauncherTest 'An absent instance permits a normal startup.' {
    $names = New-LauncherTestNames
    $active = Request-OhMySshActivation -ActivationEventName $names.ActivationEventName `
        -InstanceMutexName $names.InstanceMutexName -StartupTimeoutMilliseconds 500 -PollMilliseconds 10
    Assert-LauncherCondition (-not $active) 'An absent synthetic instance was treated as running.'
}

Invoke-LauncherTest 'An existing instance is activated without a build or launch.' {
    $names = New-LauncherTestNames
    $state = New-LauncherTestState $names
    $state.InstanceMutex = [System.Threading.Mutex]::new($true, $names.InstanceMutexName)
    $state.ActivationEvent = [System.Threading.EventWaitHandle]::new(
        $false, [System.Threading.EventResetMode]::AutoReset, $names.ActivationEventName)
    try {
        Start-OhMySshSession -Build { $state.BuildCount++ } -Launch { $state.LaunchCount++ } `
            -ActivationEventName $names.ActivationEventName -InstanceMutexName $names.InstanceMutexName `
            -LauncherMutexName $names.LauncherMutexName -StartupTimeoutMilliseconds 500 `
            -LauncherTimeoutMilliseconds 1000 -PollMilliseconds 10
        Assert-LauncherCondition ($state.BuildCount -eq 0 -and $state.LaunchCount -eq 0) `
            'An existing instance caused a build or a second launch.'
        Assert-LauncherCondition ($state.ActivationEvent.WaitOne(0)) 'The activation event was not signaled.'
        Assert-LauncherCondition (-not $state.ActivationEvent.WaitOne(0)) `
            'Activation did not preserve the event auto-reset behavior.'
    }
    finally {
        Close-LauncherTestState $state
    }
}

Invoke-LauncherTest 'Startup waits for a delayed activation event.' {
    $names = New-LauncherTestNames
    $state = New-LauncherTestState $names
    $state.InstanceMutex = [System.Threading.Mutex]::new($true, $names.InstanceMutexName)
    $invocation = $null
    try {
        $invocation = Start-LauncherTestInvocation $names @'
param($State)
Request-OhMySshActivation -ActivationEventName $State.ActivationEventName `
    -InstanceMutexName $State.InstanceMutexName -StartupTimeoutMilliseconds 2000 -PollMilliseconds 10
'@ $state
        Assert-LauncherTestPending $invocation
        $state.ActivationEvent = [System.Threading.EventWaitHandle]::new(
            $false, [System.Threading.EventResetMode]::AutoReset, $names.ActivationEventName)
        $result = @(Complete-LauncherTestInvocation $invocation)
        Assert-LauncherCondition ($result.Count -eq 1 -and $result[0] -eq $true) `
            'The delayed synthetic instance was not activated.'
        Assert-LauncherCondition ($state.ActivationEvent.WaitOne(0)) 'The delayed event was not signaled.'
    }
    finally {
        Close-LauncherTestInvocation $invocation
        Close-LauncherTestState $state
    }
}

Invoke-LauncherTest 'An unready instance times out without a build or launch.' {
    $names = New-LauncherTestNames
    $state = New-LauncherTestState $names
    $state.InstanceMutex = [System.Threading.Mutex]::new($true, $names.InstanceMutexName)
    $failed = $false
    try {
        try {
            Start-OhMySshSession -Build { $state.BuildCount++ } -Launch { $state.LaunchCount++ } `
                -ActivationEventName $names.ActivationEventName -InstanceMutexName $names.InstanceMutexName `
                -LauncherMutexName $names.LauncherMutexName -StartupTimeoutMilliseconds 80 `
                -LauncherTimeoutMilliseconds 1000 -PollMilliseconds 10
        }
        catch {
            $failed = $true
        }
        Assert-LauncherCondition $failed 'An unready synthetic instance did not report a startup timeout.'
        Assert-LauncherCondition ($state.BuildCount -eq 0 -and $state.LaunchCount -eq 0) `
            'An unready instance caused a build or a second launch.'
    }
    finally {
        Close-LauncherTestState $state
    }
}

Invoke-LauncherTest 'Startup can continue after an instance disappears.' {
    $names = New-LauncherTestNames
    $state = New-LauncherTestState $names
    $state.InstanceMutex = [System.Threading.Mutex]::new($true, $names.InstanceMutexName)
    $invocation = $null
    try {
        $invocation = Start-LauncherTestInvocation $names @'
param($State)
Request-OhMySshActivation -ActivationEventName $State.ActivationEventName `
    -InstanceMutexName $State.InstanceMutexName -StartupTimeoutMilliseconds 2000 -PollMilliseconds 10
'@ $state
        Assert-LauncherTestPending $invocation
        Close-OwnedTestMutex $state.InstanceMutex
        $state.InstanceMutex = $null
        $result = @(Complete-LauncherTestInvocation $invocation)
        Assert-LauncherCondition ($result.Count -eq 1 -and $result[0] -eq $false) `
            'The disappeared synthetic instance still prevented startup.'
    }
    finally {
        Close-LauncherTestInvocation $invocation
        Close-LauncherTestState $state
    }
}

Invoke-LauncherTest 'A competing launcher activates an instance without building.' {
    $names = New-LauncherTestNames
    $state = New-LauncherTestState $names
    $gate = [System.Threading.Mutex]::new($true, $names.LauncherMutexName)
    $invocation = $null
    try {
        $invocation = Start-LauncherTestInvocation $names @'
param($State)
Start-OhMySshSession -Build { $State.BuildCount++ } -Launch { $State.LaunchCount++ } `
    -ActivationEventName $State.ActivationEventName -InstanceMutexName $State.InstanceMutexName `
    -LauncherMutexName $State.LauncherMutexName -StartupTimeoutMilliseconds 2000 `
    -LauncherTimeoutMilliseconds 3000 -PollMilliseconds 10
'@ $state
        Assert-LauncherTestPending $invocation
        Assert-LauncherCondition ($state.BuildCount -eq 0 -and $state.LaunchCount -eq 0) `
            'A competing launcher built before acquiring the launcher lock.'
        $state.InstanceMutex = [System.Threading.Mutex]::new($true, $names.InstanceMutexName)
        $state.ActivationEvent = [System.Threading.EventWaitHandle]::new(
            $false, [System.Threading.EventResetMode]::AutoReset, $names.ActivationEventName)
        $null = Complete-LauncherTestInvocation $invocation
        Assert-LauncherCondition ($state.BuildCount -eq 0 -and $state.LaunchCount -eq 0) `
            'A competing launcher built or launched while an instance became ready.'
        Assert-LauncherCondition ($state.ActivationEvent.WaitOne(0)) 'The competing launcher did not activate the instance.'
    }
    finally {
        Close-LauncherTestInvocation $invocation
        Close-LauncherTestState $state
        Close-OwnedTestMutex $gate
    }
}

Invoke-LauncherTest 'A waiting launcher builds and launches once after acquiring the lock.' {
    $names = New-LauncherTestNames
    $state = New-LauncherTestState $names
    $gate = [System.Threading.Mutex]::new($true, $names.LauncherMutexName)
    $invocation = $null
    try {
        $invocation = Start-LauncherTestInvocation $names @'
param($State)
try {
    Start-OhMySshSession -Build { $State.BuildCount++ } -Launch {
        $State.LaunchCount++
        $State.InstanceMutex = [System.Threading.Mutex]::new($true, $State.InstanceMutexName)
        $State.ActivationEvent = [System.Threading.EventWaitHandle]::new(
            $false, [System.Threading.EventResetMode]::AutoReset, $State.ActivationEventName)
    } -ActivationEventName $State.ActivationEventName -InstanceMutexName $State.InstanceMutexName `
        -LauncherMutexName $State.LauncherMutexName -StartupTimeoutMilliseconds 2000 `
        -LauncherTimeoutMilliseconds 3000 -PollMilliseconds 10
}
finally {
    if ($null -ne $State.ActivationEvent) { $State.ActivationEvent.Dispose() }
    if ($null -ne $State.InstanceMutex) {
        $State.InstanceMutex.ReleaseMutex()
        $State.InstanceMutex.Dispose()
    }
}
'@ $state
        Assert-LauncherTestPending $invocation
        Assert-LauncherCondition ($state.BuildCount -eq 0 -and $state.LaunchCount -eq 0) `
            'A waiting launcher built before acquiring the lock.'
        Close-OwnedTestMutex $gate
        $gate = $null
        $null = Complete-LauncherTestInvocation $invocation
        Assert-LauncherCondition ($state.BuildCount -eq 1 -and $state.LaunchCount -eq 1) `
            'A normal synthetic startup did not build and launch exactly once.'
    }
    finally {
        Close-LauncherTestInvocation $invocation
        Close-OwnedTestMutex $gate
    }
}

Invoke-LauncherTest 'The launcher retains its lock until the new instance can activate.' {
    $names = New-LauncherTestNames
    $state = New-LauncherTestState $names
    $state.LaunchStarted = [System.Threading.EventWaitHandle]::new(
        $false, [System.Threading.EventResetMode]::ManualReset)
    $gate = [System.Threading.Mutex]::new($false, $names.LauncherMutexName)
    $invocation = $null
    $activationEvent = $null
    $ownsGate = $false
    try {
        $invocation = Start-LauncherTestInvocation $names @'
param($State)
try {
    Start-OhMySshSession -Build { $State.BuildCount++ } -Launch {
        $State.LaunchCount++
        $State.InstanceMutex = [System.Threading.Mutex]::new($true, $State.InstanceMutexName)
        [void] $State.LaunchStarted.Set()
    } -ActivationEventName $State.ActivationEventName -InstanceMutexName $State.InstanceMutexName `
        -LauncherMutexName $State.LauncherMutexName -StartupTimeoutMilliseconds 2000 `
        -LauncherTimeoutMilliseconds 3000 -PollMilliseconds 10
}
finally {
    if ($null -ne $State.InstanceMutex) {
        $State.InstanceMutex.ReleaseMutex()
        $State.InstanceMutex.Dispose()
    }
}
'@ $state
        Assert-LauncherCondition ($state.LaunchStarted.WaitOne(5000)) 'The synthetic launch did not begin.'
        Assert-LauncherTestPending $invocation
        $ownsGate = $gate.WaitOne(0)
        Assert-LauncherCondition (-not $ownsGate) 'The launcher released its lock before the instance was ready.'
        $activationEvent = [System.Threading.EventWaitHandle]::new(
            $false, [System.Threading.EventResetMode]::AutoReset, $names.ActivationEventName)
        $null = Complete-LauncherTestInvocation $invocation
        Assert-LauncherCondition ($state.BuildCount -eq 1 -and $state.LaunchCount -eq 1) `
            'The delayed startup did not build and launch exactly once.'
        Assert-LauncherCondition ($activationEvent.WaitOne(0)) 'The delayed new instance was not activated.'
        $ownsGate = $gate.WaitOne(0)
        Assert-LauncherCondition $ownsGate 'The launcher did not release its lock after the instance became ready.'
    }
    finally {
        if ($ownsGate) { $gate.ReleaseMutex() }
        Close-LauncherTestInvocation $invocation
        if ($null -ne $activationEvent) { $activationEvent.Dispose() }
        $state.LaunchStarted.Dispose()
        $gate.Dispose()
    }
}

foreach ($failureStage in @('build', 'launch')) {
    Invoke-LauncherTest "A failed $failureStage releases the launcher lock." {
    $names = New-LauncherTestNames
    $state = New-LauncherTestState $names
    $state.FailureStage = $failureStage
    $state.FailureObserved = $false
    # Retain an observer handle and probe from another thread, avoiding reentrant mutex acquisition.
    $gate = [System.Threading.Mutex]::new($false, $names.LauncherMutexName)
    $invocation = $null
    $ownsGate = $false
    try {
        $invocation = Start-LauncherTestInvocation $names @'
param($State)
try {
    Start-OhMySshSession -Build {
        $State.BuildCount++
        if ($State.FailureStage -eq 'build') { throw 'Synthetic callback failure.' }
    } -Launch {
        $State.LaunchCount++
        throw 'Synthetic callback failure.'
    } -ActivationEventName $State.ActivationEventName -InstanceMutexName $State.InstanceMutexName `
        -LauncherMutexName $State.LauncherMutexName -StartupTimeoutMilliseconds 500 `
        -LauncherTimeoutMilliseconds 1000 -PollMilliseconds 10
}
catch {
    $State.FailureObserved = $_.Exception.Message -like '*Synthetic callback failure.*'
}
'@ $state
        $null = Complete-LauncherTestInvocation $invocation
        Assert-LauncherCondition $state.FailureObserved 'The synthetic callback failure did not reach the caller.'
        $expectedLaunchCount = if ($failureStage -eq 'launch') { 1 } else { 0 }
        Assert-LauncherCondition ($state.BuildCount -eq 1 -and $state.LaunchCount -eq $expectedLaunchCount) `
            'The failed callback caused an unexpected build or launch count.'
        $ownsGate = $gate.WaitOne(0)
        Assert-LauncherCondition $ownsGate 'A failed callback left the launcher lock held.'
    }
    finally {
        if ($ownsGate) { $gate.ReleaseMutex() }
        Close-LauncherTestInvocation $invocation
        $gate.Dispose()
    }
    }
}

Write-Host "Launcher verification passed: $passed tests; no application builds or real app launches."
