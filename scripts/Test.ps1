[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Target = "",

    [string]$Configuration = "",

    [string]$Filter = "",

    [switch]$NoBuild,

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
$testTarget = if ([string]::IsNullOrWhiteSpace($Target)) {
    Join-Path $repoRoot "Vigilo.sln"
} else {
    $Target
}

$dotnetArguments = [System.Collections.Generic.List[string]]::new()
$dotnetArguments.Add("test")
$dotnetArguments.Add($testTarget)
$dotnetArguments.Add("--nologo")
if (-not [string]::IsNullOrWhiteSpace($Configuration)) {
    $dotnetArguments.Add("--configuration")
    $dotnetArguments.Add($Configuration)
}
if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $dotnetArguments.Add("--filter")
    $dotnetArguments.Add($Filter)
}
if ($NoBuild) {
    $dotnetArguments.Add("--no-build")
}
elseif ($NoRestore) {
    $dotnetArguments.Add("--no-restore")
}

& dotnet @dotnetArguments
exit $LASTEXITCODE
