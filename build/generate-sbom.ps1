param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^\d+\.\d+\.\d+$")]
    [string]$Version,
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = "artifacts",
    [string]$FileName = "",
    [switch]$RunUnderBuildLock
)

$ErrorActionPreference = "Stop"
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
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $repo $OutputDirectory
}

if ([string]::IsNullOrWhiteSpace($FileName)) {
    $FileName = "Vigilo-v$Version-$Runtime.cdx.json"
}

if ([System.IO.Path]::GetFileName($FileName) -ne $FileName) {
    throw "FileName must contain a file name only."
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

Push-Location $repo
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restore the pinned CycloneDX tool."
    }

    dotnet tool run dotnet-CycloneDX -- `
        $appProject `
        --recursive `
        --include-project-references `
        --framework net10.0-windows `
        --runtime $Runtime `
        --disable-package-restore `
        --output $outputRoot `
        --filename $FileName `
        --output-format Json `
        --spec-version 1.7 `
        --set-name Vigilo `
        --set-version $Version `
        --set-type Application
    if ($LASTEXITCODE -ne 0) {
        throw "CycloneDX SBOM generation failed."
    }

    $sbomPath = Join-Path $outputRoot $FileName
    if (-not (Test-Path -LiteralPath $sbomPath -PathType Leaf)) {
        throw "CycloneDX did not create the expected SBOM: $sbomPath"
    }

    $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
    if ($sbom.bomFormat -ne "CycloneDX") {
        throw "The generated document is not a CycloneDX SBOM."
    }

    if ($sbom.specVersion -ne "1.7") {
        throw "The generated SBOM does not use CycloneDX 1.7."
    }

    if ($sbom.metadata.component.name -ne "Vigilo" -or $sbom.metadata.component.version -ne $Version) {
        throw "The generated SBOM metadata does not identify Vigilo $Version."
    }

    $componentNames = @($sbom.components | ForEach-Object { $_.name })
    $expectedComponents = @(
        "MailKit",
        "Microsoft.EntityFrameworkCore.Sqlite",
        "Microsoft.ML.OnnxRuntimeGenAI"
    )
    $missingComponents = @($expectedComponents | Where-Object { $_ -notin $componentNames })
    if ($missingComponents.Count -gt 0) {
        throw "The generated SBOM is missing expected runtime components: $($missingComponents -join ', ')"
    }

    Write-Host "Created and validated CycloneDX SBOM $sbomPath"
} finally {
    Pop-Location
}
