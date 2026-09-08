$ErrorActionPreference = "Stop"

function Get-RepositoryBuildMutexInfo {
    param([string]$WorkingDirectory = (Get-Location).Path)

    $gitOutput = & git -C $WorkingDirectory rev-parse --show-toplevel 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to determine the Git repository root from '$WorkingDirectory'. Run this command inside a Git repository. Git reported: $($gitOutput -join ' ')"
    }

    $repositoryRoot = ($gitOutput | Select-Object -Last 1).Trim()
    if ([string]::IsNullOrWhiteSpace($repositoryRoot)) {
        throw "Git returned an empty repository root for '$WorkingDirectory'."
    }

    $normalizedRoot = [System.IO.Path]::GetFullPath($repositoryRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    if ($IsWindows -or $PSVersionTable.PSEdition -eq "Desktop") {
        $normalizedRoot = $normalizedRoot.ToUpperInvariant()
    }

    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($normalizedRoot))
    }
    finally {
        $sha256.Dispose()
    }

    $repositoryHash = ([System.BitConverter]::ToString($hashBytes) -replace "-", "").Substring(0, 24)
    $mutexPrefix = if ($IsWindows -or $PSVersionTable.PSEdition -eq "Desktop") { "Global\" } else { "" }

    [pscustomobject]@{
        RepositoryRoot = $normalizedRoot
        MutexName = "${mutexPrefix}Vigilo-Build-$repositoryHash"
    }
}

function Invoke-RepositoryBuildCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [object[]]$Arguments = @(),

        [string]$WorkingDirectory = (Get-Location).Path
    )

    if ([string]::Equals($env:GITHUB_ACTIONS, "true", [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Host "GitHub Actions detected; running without the local repository build lock."
        $global:LASTEXITCODE = 0
        & $Executable @Arguments | Out-Host
        return $(if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE })
    }

    $mutexInfo = Get-RepositoryBuildMutexInfo -WorkingDirectory $WorkingDirectory
    $mutex = [System.Threading.Mutex]::new($false, $mutexInfo.MutexName)
    $lockAcquired = $false

    try {
        try {
            $lockAcquired = $mutex.WaitOne(0)
        }
        catch [System.Threading.AbandonedMutexException] {
            $lockAcquired = $true
            Write-Host "Recovered abandoned repository build lock '$($mutexInfo.MutexName)'."
        }

        if (-not $lockAcquired) {
            Write-Host "Waiting for repository build lock '$($mutexInfo.MutexName)'..."
            try {
                $lockAcquired = $mutex.WaitOne()
            }
            catch [System.Threading.AbandonedMutexException] {
                $lockAcquired = $true
                Write-Host "Recovered abandoned repository build lock '$($mutexInfo.MutexName)'."
            }
        }

        Write-Host "Acquired repository build lock '$($mutexInfo.MutexName)'."
        $global:LASTEXITCODE = 0
        & $Executable @Arguments | Out-Host
        $commandExitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        return $commandExitCode
    }
    finally {
        if ($lockAcquired) {
            $mutex.ReleaseMutex()
            Write-Host "Released repository build lock '$($mutexInfo.MutexName)'."
        }

        $mutex.Dispose()
    }
}

function Invoke-ScriptUnderRepositoryBuildLock {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptPath,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$BoundParameters,

        [Parameter(Mandatory = $true)]
        [string]$LockedSwitchName
    )

    $hostExecutable = (Get-Process -Id $PID).Path
    $arguments = [System.Collections.Generic.List[object]]::new()
    $arguments.Add("-NoProfile")
    $arguments.Add("-File")
    $arguments.Add($ScriptPath)
    $arguments.Add("-$LockedSwitchName")

    foreach ($entry in $BoundParameters.GetEnumerator()) {
        if ($entry.Key -eq $LockedSwitchName) {
            continue
        }

        if ($entry.Value -is [System.Management.Automation.SwitchParameter]) {
            if ($entry.Value.IsPresent) {
                $arguments.Add("-$($entry.Key)")
            }
            continue
        }

        $arguments.Add("-$($entry.Key)")
        if ($entry.Value -is [System.Array]) {
            foreach ($item in $entry.Value) {
                $arguments.Add($item)
            }
        }
        else {
            $arguments.Add($entry.Value)
        }
    }

    return Invoke-RepositoryBuildCommand `
        -Executable $hostExecutable `
        -Arguments $arguments `
        -WorkingDirectory (Split-Path -Parent $ScriptPath)
}

if ($MyInvocation.InvocationName -ne ".") {
    if ($args.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$args[0])) {
        throw "Usage: .\scripts\Invoke-ExclusiveBuild.ps1 <command> <arguments>"
    }

    $executable = [string]$args[0]
    $commandArguments = if ($args.Count -gt 1) { [object[]]$args[1..($args.Count - 1)] } else { @() }
    $exitCode = Invoke-RepositoryBuildCommand -Executable $executable -Arguments $commandArguments
    exit $exitCode
}
