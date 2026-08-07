[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $Version,

  [ValidateSet("Debug", "Release")]
  [string] $Configuration = "Release",

  [int[]] $RevitYears = @(2025, 2026)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$declaredVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot "VERSION") -Raw).Trim()
$requestedVersion = $Version.Trim()
if ($requestedVersion.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
  $requestedVersion = $requestedVersion.Substring(1)
}

if ($requestedVersion -cne $declaredVersion) {
  throw "Requested version '$Version' does not match VERSION '$declaredVersion'."
}

$mcpOutput = Join-Path $repositoryRoot "src/Tripo.Revit.Mcp/bin/$Configuration/net8.0"
if (-not (Test-Path -LiteralPath (Join-Path $mcpOutput "Tripo.Revit.Mcp.dll") -PathType Leaf)) {
  throw "Required release input is missing: $(Join-Path $mcpOutput 'Tripo.Revit.Mcp.dll')"
}

foreach ($revitYear in $RevitYears) {
  $hostOutput = Join-Path $repositoryRoot "src/Tripo.Revit/bin/$Configuration/net8.0-windows/$revitYear"
  foreach ($requiredPath in @(
    (Join-Path $hostOutput "Tripo.Revit.dll"),
    (Join-Path $hostOutput "Tripo.Revit.addin"),
    (Join-Path $hostOutput "sidecar/Tripo.Revit.Mcp.dll")
  )) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
      throw "Required release input is missing for Revit $revitYear`: $requiredPath"
    }
  }
}

$artifactsDirectory = Join-Path $repositoryRoot "artifacts"
$candidateName = "tripo-revit-v$declaredVersion"
$candidateRoot = Join-Path $artifactsDirectory $candidateName
$archivePath = "$candidateRoot.zip"
$checksumPath = "$archivePath.sha256"

foreach ($generatedPath in @($candidateRoot, $archivePath, $checksumPath)) {
  if (Test-Path -LiteralPath $generatedPath) {
    Remove-Item -LiteralPath $generatedPath -Recurse -Force
  }
}

New-Item -ItemType Directory -Path (Join-Path $candidateRoot "mcp") -Force | Out-Null

foreach ($revitYear in $RevitYears) {
  $hostOutput = Join-Path $repositoryRoot "src/Tripo.Revit/bin/$Configuration/net8.0-windows/$revitYear"
  $addinRoot = Join-Path $candidateRoot "Addins/$revitYear"
  $pluginRoot = Join-Path $addinRoot "TripoRevit"
  New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
  Get-ChildItem -LiteralPath $hostOutput |
    Where-Object { $_.Name -ne "Tripo.Revit.addin" } |
    Copy-Item -Destination $pluginRoot -Recurse -Force
  Copy-Item -LiteralPath (Join-Path $hostOutput "Tripo.Revit.addin") -Destination $addinRoot
}

Copy-Item -Path (Join-Path $mcpOutput "*") -Destination (Join-Path $candidateRoot "mcp") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.md") -Destination $candidateRoot
Copy-Item -LiteralPath (Join-Path $repositoryRoot "README.zh-CN.md") -Destination $candidateRoot

Compress-Archive -LiteralPath $candidateRoot -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archivePath))" |
  Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Output $archivePath
Write-Output $checksumPath
