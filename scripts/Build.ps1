[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Target = "",

    [string]$Configuration = "",

    [switch]$NoRestore,

    [switch]$RunUnderBuildLock
)

$ErrorActionPreference = "Stop"

if (-not $RunUnderBuildLock) {
    . (Join-Path $PSScriptRoot "Invoke-ExclusiveBuild.ps1")
    $exitCode = Invoke-ScriptUnderRepositoryBuildLock `
        -ScriptPath $PSCommandPath `
        -BoundParameters $PSBoundParameters `
        -LockedSwitchName "RunUnderBuildLock"
    exit $exitCode
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$buildTarget = if ([string]::IsNullOrWhiteSpace($Target)) {
    Join-Path $repoRoot "Vigilo.sln"
} else {
    $Target
}

$dotnetArguments = [System.Collections.Generic.List[string]]::new()
$dotnetArguments.Add("build")
$dotnetArguments.Add($buildTarget)
$dotnetArguments.Add("--nologo")
if (-not [string]::IsNullOrWhiteSpace($Configuration)) {
    $dotnetArguments.Add("--configuration")
    $dotnetArguments.Add($Configuration)
}
if ($NoRestore) {
    $dotnetArguments.Add("--no-restore")
}

& dotnet @dotnetArguments
exit $LASTEXITCODE
