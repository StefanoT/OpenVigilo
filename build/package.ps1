param(
    [string]$Configuration = "Release",
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [string]$Version = "0.0.0",
    [string]$Tag = "",
    [switch]$NoRestore,
    [switch]$RunUnderBuildLock
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$repo = Split-Path -Parent $PSScriptRoot

if (-not $RunUnderBuildLock) {
    . (Join-Path $repo "scripts\Invoke-ExclusiveBuild.ps1")
    $exitCode = Invoke-ScriptUnderRepositoryBuildLock `
        -ScriptPath $PSCommandPath `
        -BoundParameters $PSBoundParameters `
        -LockedSwitchName "RunUnderBuildLock"
    exit $exitCode
}

$appProject = Join-Path $repo "src\Vigilo.App\Vigilo.App.csproj"
$packageProject = Join-Path $repo "packaging\Vigilo.Package\Vigilo.Package.wixproj"
$setupProject = Join-Path $repo "packaging\Vigilo.Setup\Vigilo.Setup.wixproj"
$artifacts = Join-Path $repo "artifacts"
$publishDir = Join-Path $artifacts "publish\$Runtime"
$installerDir = Join-Path $artifacts "installer"

if ($Tag) {
    if ($Tag -notmatch '^v(?<release>\d+\.\d+\.\d+)$') {
        throw "Release tag '$Tag' is invalid. Expected v<major>.<minor>.<patch>, for example v0.1.0."
    }

    $Version = $Matches.release
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Release version '$Version' is invalid. Expected <major>.<minor>.<patch>, for example 0.1.0."
}

$versionParts = $Version.Split('.') | ForEach-Object { [uint64]::Parse($_) }
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) {
    throw "Release version '$Version' cannot be represented safely as a Windows Installer version (major/minor <= 255, patch <= 65535)."
}

$portableName = "Vigilo-Portable-$Version-x64.zip"
$setupName = "Vigilo-Setup-$Version-x64.exe"
$sbomName = "Vigilo-$Version-$Runtime.cdx.json"
$portablePath = Join-Path $artifacts $portableName
$setupPath = Join-Path $artifacts $setupName
$sbomPath = Join-Path $artifacts $sbomName
$checksumsPath = Join-Path $artifacts "SHA256SUMS.txt"

function Invoke-Checked([scriptblock]$Command, [string]$FailureMessage) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit code $LASTEXITCODE)."
    }
}

function Get-Sha256Hex([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return -join ($sha256.ComputeHash($stream) | ForEach-Object { $_.ToString("x2") })
    } finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

foreach ($path in @($publishDir, $installerDir, $portablePath, $setupPath, $checksumsPath, $sbomPath)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $publishDir, $installerDir -Force | Out-Null

if (-not $NoRestore) {
    Invoke-Checked { dotnet restore $appProject --locked-mode } "Locked application restore failed"
    Invoke-Checked { dotnet restore $packageProject } "WiX package restore failed"
    Invoke-Checked { dotnet restore $setupProject } "WiX bundle restore failed"
}

Invoke-Checked {
    dotnet publish $appProject `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --no-restore `
        --output $publishDir `
        -p:Version=$Version `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:DebugType=None `
        -p:DebugSymbols=false
} "Vigilo publish failed"

$unexpectedPublishFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.Extension -in @('.pdb', '.wixpdb', '.msi', '.zip') -or $_.Name -match '\.(download|partial|tmp)$' }
if ($unexpectedPublishFiles) {
    throw "Publish output contains files that cannot ship: $($unexpectedPublishFiles.FullName -join ', ')"
}

if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Vigilo.App.exe"))) {
    throw "Publish output does not contain Vigilo.App.exe."
}

$embeddedModelFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.Name -in @('model.onnx', 'model.onnx.data') -or $_.Extension -eq '.litertlm' }
if ($embeddedModelFiles) {
    throw "Release output must not contain model files: $($embeddedModelFiles.FullName -join ', ')"
}

Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $portablePath -CompressionLevel Optimal

$packageOutput = Join-Path $installerDir "package"
$setupOutput = Join-Path $installerDir "setup"
New-Item -ItemType Directory -Path $packageOutput, $setupOutput -Force | Out-Null

Invoke-Checked {
    dotnet build $packageProject `
        --configuration $Configuration `
        --no-restore `
        -p:Version=$Version `
        -p:PublishDirectory=$publishDir `
        -p:OutputPath=$packageOutput
} "WiX MSI build failed"

$msiPath = Get-ChildItem -LiteralPath $packageOutput -Filter '*.msi' -Recurse -File | Select-Object -First 1
if (-not $msiPath) {
    throw "WiX package build did not produce an MSI."
}

Invoke-Checked {
    dotnet build $setupProject `
        --configuration $Configuration `
        --no-restore `
        -p:Version=$Version `
        -p:MsiPath=$($msiPath.FullName) `
        -p:OutputPath=$setupOutput
} "WiX setup bundle build failed"

$builtSetup = Get-ChildItem -LiteralPath $setupOutput -Filter '*.exe' -Recurse -File | Select-Object -First 1
if (-not $builtSetup) {
    throw "WiX setup bundle build did not produce an EXE."
}
Copy-Item -LiteralPath $builtSetup.FullName -Destination $setupPath

& (Join-Path $PSScriptRoot "generate-sbom.ps1") `
    -Version $Version `
    -Runtime $Runtime `
    -OutputDirectory $artifacts `
    -FileName $sbomName `
    -RunUnderBuildLock

$checksumLines = @(
    "$(Get-Sha256Hex $setupPath)  $setupName",
    "$(Get-Sha256Hex $portablePath)  $portableName"
)
[System.IO.File]::WriteAllText($checksumsPath, ($checksumLines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    if ($line -notmatch '^(?<hash>[0-9a-f]{64})  (?<name>.+)$') {
        throw "Invalid checksum line: $line"
    }

    $target = Join-Path $artifacts $Matches.name
    if (-not (Test-Path -LiteralPath $target) -or (Get-Sha256Hex $target) -ne $Matches.hash) {
        throw "Checksum verification failed for '$($Matches.name)'."
    }
}

Write-Host "Release version: $Version"
Write-Host "Publish directory: $publishDir"
Write-Host "Created: $setupPath"
Write-Host "Created: $portablePath"
Write-Host "Created: $checksumsPath"
Write-Host "Created: $sbomPath"
