[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$companionRoot = Join-Path $repositoryRoot "apps\companion"
$sourceRoot = Join-Path $companionRoot "src"
$outputRoot = Join-Path $companionRoot "dist"
$normalizedVersion = $Version.Trim()
if ($normalizedVersion -and $normalizedVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw "VersionはSemVer形式で指定してください（例: 1.0.0）: $Version"
}
$packageName = if ($normalizedVersion) {
    "VehicleManagement.Companion-v$normalizedVersion-portable-win-x64"
} else {
    "VehicleManagement.Companion-portable-win-x64"
}
$stagingRoot = Join-Path $outputRoot ".publish"
$packageRoot = Join-Path $outputRoot $packageName
$zipPath = Join-Path $outputRoot "$packageName.zip"

$companionProject = Join-Path $sourceRoot "VehicleManagement.Companion\VehicleManagement.Companion.csproj"
$legacyHostProject = Join-Path $sourceRoot "VehicleManagement.LegacyHost\VehicleManagement.LegacyHost.csproj"
$probeProject = Join-Path $sourceRoot "VehicleManagement.LegacyAutomationProbe\VehicleManagement.LegacyAutomationProbe.csproj"

$companionPublish = Join-Path $stagingRoot "Companion"
$legacyHostPublish = Join-Path $stagingRoot "LegacyHost"
$probePublish = Join-Path $stagingRoot "Probe"

$fullCompanionRoot = [IO.Path]::GetFullPath($companionRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$fullOutputRoot = [IO.Path]::GetFullPath($outputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not $fullOutputRoot.StartsWith("$fullCompanionRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)) {
    throw "出力先がCompanionディレクトリ配下ではありません: $outputRoot"
}

if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

function Publish-Project {
    param(
        [Parameter(Mandatory)]
        [string]$Project,
        [Parameter(Mandatory)]
        [string]$Runtime,
        [Parameter(Mandatory)]
        [string]$PlatformTarget,
        [Parameter(Mandatory)]
        [string]$Destination,
        [switch]$SkipLegacyHostCopy
    )

    Write-Host "Publishing $Project ($Runtime, $Configuration, self-contained)..."
    $arguments = @(
        "publish",
        $Project,
        "--configuration", $Configuration,
        "--runtime", $Runtime,
        "--self-contained", "true",
        "--output", $Destination,
        "-p:PlatformTarget=$PlatformTarget",
        "-p:PublishSingleFile=false",
        "-p:DebugSymbols=false",
        "-p:DebugType=None"
    )
    if ($SkipLegacyHostCopy) {
        $arguments += "-p:SkipLegacyHostCopy=true"
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed ($LASTEXITCODE): $Project"
    }
}

Publish-Project $companionProject "win-x64" "x64" $companionPublish -SkipLegacyHostCopy
Publish-Project $legacyHostProject "win-x64" "x64" $legacyHostPublish
Publish-Project $probeProject "win-x86" "x86" $probePublish

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -Path (Join-Path $companionPublish "*") -Destination $packageRoot -Recurse -Force

$packagedLegacyHostRoot = Join-Path $packageRoot "LegacyHost"
if (Test-Path -LiteralPath $packagedLegacyHostRoot) {
    Remove-Item -LiteralPath $packagedLegacyHostRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packagedLegacyHostRoot -Force | Out-Null
Copy-Item -Path (Join-Path $legacyHostPublish "*") -Destination $packagedLegacyHostRoot -Recurse -Force

$packagedProbeRoot = Join-Path $packagedLegacyHostRoot "Probe"
New-Item -ItemType Directory -Path $packagedProbeRoot -Force | Out-Null
Copy-Item -Path (Join-Path $probePublish "*") -Destination $packagedProbeRoot -Recurse -Force

$developmentFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Force |
    Where-Object { $_.Extension.ToLowerInvariant() -in @(".pdb", ".xml") }
if ($developmentFiles) {
    $developmentFiles | Remove-Item -Force
}

$developmentDirectories = Get-ChildItem -LiteralPath $packageRoot -Recurse -Directory -Force |
    Where-Object { $_.Name -in @("bin", "obj") } |
    Sort-Object FullName -Descending
if ($developmentDirectories) {
    $developmentDirectories | Remove-Item -Recurse -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

Remove-Item -LiteralPath $stagingRoot -Recurse -Force

$zipInfo = Get-Item -LiteralPath $zipPath
Write-Host "Portable package: $packageRoot"
Write-Host "Portable ZIP: $zipPath ($($zipInfo.Length) bytes)"
