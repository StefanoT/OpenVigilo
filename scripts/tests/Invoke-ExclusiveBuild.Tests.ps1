[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Start-CoordinatorProcess {
    param(
        [string]$WorkingDirectory,
        [string]$Command,
        [string[]]$Arguments,
        [string]$OutputPath,
        [string]$ErrorPath
    )

    $argumentList = @("-NoProfile", "-File", $coordinator, $Command) + $Arguments
    Start-Process -FilePath $powershellPath -ArgumentList $argumentList -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $OutputPath -RedirectStandardError $ErrorPath -PassThru -WindowStyle Hidden
}

$repoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "The coordinator tests must run from a Git checkout."
}

$coordinator = Join-Path $repoRoot "scripts\Invoke-ExclusiveBuild.ps1"
$powershellPath = (Get-Process -Id $PID).Path
$powershellCommand = [System.IO.Path]::GetFileName($powershellPath)
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "Vigilo-BuildLock-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    $probeScript = Join-Path $testRoot "probe.ps1"
    $killScript = Join-Path $testRoot "kill-owner.ps1"
    $primaryRepo = Join-Path $testRoot "primary-repository"
    $otherRepo = Join-Path $testRoot "other-repository"
    New-Item -ItemType Directory -Path $primaryRepo, $otherRepo | Out-Null
    & git -C $primaryRepo init --quiet
    Assert-True ($LASTEXITCODE -eq 0) "Unable to create the primary temporary repository."
    & git -C $otherRepo init --quiet
    Assert-True ($LASTEXITCODE -eq 0) "Unable to create the comparison temporary repository."
    @'
param([string]$LogPath, [string]$Name, [int]$DelayMilliseconds = 0, [string]$GatePath = "")
Add-Content -LiteralPath $LogPath -Value "$Name-enter"
if (-not [string]::IsNullOrWhiteSpace($GatePath)) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $GatePath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
}
elseif ($DelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $DelayMilliseconds }
Add-Content -LiteralPath $LogPath -Value "$Name-exit"
'@ | Set-Content -LiteralPath $probeScript -Encoding utf8
    'Stop-Process -Id $PID -Force' | Set-Content -LiteralPath $killScript -Encoding utf8

    $orderLog = Join-Path $testRoot "order.log"
    $firstOut = Join-Path $testRoot "first.out"
    $firstErr = Join-Path $testRoot "first.err"
    $secondOut = Join-Path $testRoot "second.out"
    $secondErr = Join-Path $testRoot "second.err"

    $gatePath = Join-Path $testRoot "release-first"
    $first = Start-CoordinatorProcess $primaryRepo $powershellCommand @("-NoProfile", "-File", $probeScript, $orderLog, "first", "0", $gatePath) $firstOut $firstErr
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((-not (Test-Path $orderLog) -or (Get-Content $orderLog -ErrorAction SilentlyContinue) -notcontains "first-enter") -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    Assert-True (-not $first.HasExited) "The first protected process exited before the concurrency test began."

    $second = Start-CoordinatorProcess $primaryRepo $powershellCommand @("-NoProfile", "-File", $probeScript, $orderLog, "second", "0") $secondOut $secondErr
    $waitDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 50
        $secondOutput = if (Test-Path $secondOut) { "$(Get-Content $secondOut -Raw -ErrorAction SilentlyContinue)" } else { "" }
    } while (-not $secondOutput.Contains("Waiting for repository build lock") -and [DateTime]::UtcNow -lt $waitDeadline)
    Set-Content -LiteralPath $gatePath -Value "release"
    $first.WaitForExit(30000) | Out-Null
    $second.WaitForExit(30000) | Out-Null
    Assert-True ($first.HasExited -and $second.HasExited) "The serialization test processes did not finish."
    $serializationDiagnostics = "first exit: $($first.ExitCode); first stdout: $(Get-Content $firstOut -Raw -ErrorAction SilentlyContinue); first stderr: $(Get-Content $firstErr -Raw -ErrorAction SilentlyContinue); second exit: $($second.ExitCode); second stdout: $(Get-Content $secondOut -Raw -ErrorAction SilentlyContinue); second stderr: $(Get-Content $secondErr -Raw -ErrorAction SilentlyContinue)"
    $firstSucceeded = $null -eq $first.ExitCode -or $first.ExitCode -eq 0
    $secondSucceeded = $null -eq $second.ExitCode -or $second.ExitCode -eq 0
    Assert-True ($firstSucceeded -and $secondSucceeded) "A serialization test process failed. $serializationDiagnostics"
    $order = @(Get-Content $orderLog)
    Assert-True (($order -join ",") -eq "first-enter,first-exit,second-enter,second-exit") "Protected sections overlapped: $($order -join ', ')."
    Assert-True ((Get-Content $secondOut -Raw).Contains("Waiting for repository build lock")) "The second process did not report that it waited."

    Push-Location $primaryRepo
    try {
        & $coordinator $powershellCommand -NoProfile -Command "exit 23"
        Assert-True ($LASTEXITCODE -eq 23) "The wrapped non-zero exit code was not propagated."

        & $coordinator $powershellCommand -NoProfile -File $probeScript $orderLog "after-failure" "0"
        Assert-True ($LASTEXITCODE -eq 0) "The mutex was not released after a wrapped command failed."
    }
    finally {
        Pop-Location
    }

    $abandonedOut = Join-Path $testRoot "abandoned.out"
    $abandonedErr = Join-Path $testRoot "abandoned.err"
    $abandoned = Start-CoordinatorProcess $primaryRepo $killScript @() $abandonedOut $abandonedErr
    $abandoned.WaitForExit(30000) | Out-Null
    Assert-True $abandoned.HasExited "The abandoned-lock owner did not terminate."

    $recoveryOut = Join-Path $testRoot "recovery.out"
    $recoveryErr = Join-Path $testRoot "recovery.err"
    $recovery = Start-CoordinatorProcess $primaryRepo $powershellCommand @("-NoProfile", "-File", $probeScript, $orderLog, "after-abandon", "0") $recoveryOut $recoveryErr
    $recovery.WaitForExit(30000) | Out-Null
    $recoverySucceeded = $null -eq $recovery.ExitCode -or $recovery.ExitCode -eq 0
    $recoveryDiagnostics = "exit: $($recovery.ExitCode); stdout: $(Get-Content $recoveryOut -Raw -ErrorAction SilentlyContinue); stderr: $(Get-Content $recoveryErr -Raw -ErrorAction SilentlyContinue)"
    Assert-True ($recovery.HasExited -and $recoverySucceeded) "An abandoned mutex blocked a later command. $recoveryDiagnostics"

    . $coordinator
    $mainMutexName = (Get-RepositoryBuildMutexInfo -WorkingDirectory $primaryRepo).MutexName
    $otherMutexName = (Get-RepositoryBuildMutexInfo -WorkingDirectory $otherRepo).MutexName
    Assert-True ($mainMutexName -ne $otherMutexName) "Different repository paths generated the same mutex name."

    Write-Host "Coordinator tests: Passed (serialization, waiting, exit propagation, failure release, abandonment recovery, path uniqueness)."
}
finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
