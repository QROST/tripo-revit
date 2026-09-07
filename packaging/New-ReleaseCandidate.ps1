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
if ($declaredVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
  throw "VERSION must contain a canonical release version."
}
if ($RevitYears.Count -eq 0 -or
    @($RevitYears | Select-Object -Unique).Count -ne $RevitYears.Count -or
    @($RevitYears | Where-Object { $_ -notin @(2025, 2026) }).Count -ne 0) {
  throw "RevitYears must contain distinct supported years (2025 and/or 2026)."
}
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
    throw "Candidate already exists: $generatedPath. Preserve or move it before rebuilding."
  }
}

New-Item -ItemType Directory -Path $artifactsDirectory -Force | Out-Null
$lockPath = Join-Path $artifactsDirectory "$candidateName.lock"
# CreateNew excludes a second builder. A crash leaves a visible lock for review.
$candidateLock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$stage = Join-Path $artifactsDirectory ".candidate-$([Guid]::NewGuid().ToString('N'))"
$stagedRoot = Join-Path $stage $candidateName
$stagedArchive = Join-Path $stage "$candidateName.zip"
$stagedChecksum = "$stagedArchive.sha256"
$publishedPaths = [Collections.Generic.List[string]]::new()
try {
  New-Item -ItemType Directory -Path (Join-Path $stagedRoot "mcp") -Force | Out-Null

  foreach ($revitYear in $RevitYears) {
    $hostOutput = Join-Path $repositoryRoot "src/Tripo.Revit/bin/$Configuration/net8.0-windows/$revitYear"
    $addinRoot = Join-Path $stagedRoot "Addins/$revitYear"
    $pluginRoot = Join-Path $addinRoot "TripoRevit"
    New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $hostOutput |
      Where-Object { $_.Name -ne "Tripo.Revit.addin" } |
      Copy-Item -Destination $pluginRoot -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $hostOutput "Tripo.Revit.addin") -Destination $addinRoot
  }

  Copy-Item -Path (Join-Path $mcpOutput "*") -Destination (Join-Path $stagedRoot "mcp") -Recurse -Force
  foreach ($document in @("README.md", "README.zh-CN.md", "LICENSE", "NOTICE")) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $document) -Destination $stagedRoot
  }

  Compress-Archive -LiteralPath $stagedRoot -DestinationPath $stagedArchive -CompressionLevel Optimal
  $hash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
  "$hash  $([IO.Path]::GetFileName($archivePath))" |
    Set-Content -LiteralPath $stagedChecksum -Encoding ascii

  # Same-volume moves refuse overwrites; the ZIP appears only after completion.
  [IO.Directory]::Move($stagedRoot, $candidateRoot)
  $publishedPaths.Add($candidateRoot)
  [IO.File]::Move($stagedChecksum, $checksumPath)
  $publishedPaths.Add($checksumPath)
  [IO.File]::Move($stagedArchive, $archivePath)
  $publishedPaths.Add($archivePath)
} catch {
  foreach ($publishedPath in $publishedPaths) {
    Remove-Item -LiteralPath $publishedPath -Recurse -Force
  }
  throw
} finally {
  try {
    if (Test-Path -LiteralPath $stage) {
      Remove-Item -LiteralPath $stage -Recurse -Force
    }
  } finally {
    $candidateLock.Dispose()
    Remove-Item -LiteralPath $lockPath -Force
  }
}

Write-Output $archivePath
Write-Output $checksumPath
