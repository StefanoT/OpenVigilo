[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Target = "",

    [string]$Configuration = "",

    [string]$Filter = "",

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

$buildParameters = @{ RunUnderBuildLock = $true }
$testParameters = @{ RunUnderBuildLock = $true; NoBuild = $true }
if (-not [string]::IsNullOrWhiteSpace($Target)) {
    $buildParameters.Target = $Target
    $testParameters.Target = $Target
}
if (-not [string]::IsNullOrWhiteSpace($Configuration)) {
    $buildParameters.Configuration = $Configuration
    $testParameters.Configuration = $Configuration
}
if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $testParameters.Filter = $Filter
}

& (Join-Path $PSScriptRoot "Build.ps1") @buildParameters
$buildExitCode = $LASTEXITCODE
if ($buildExitCode -ne 0) {
    exit $buildExitCode
}

& (Join-Path $PSScriptRoot "Test.ps1") @testParameters
exit $LASTEXITCODE
