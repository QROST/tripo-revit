[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$sourceScript = Join-Path $PSScriptRoot "New-ReleaseCandidate.ps1"
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "tripo-candidate-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Assert-True([bool] $Condition, [string] $Message) {
  if (-not $Condition) { throw $Message }
}

function Write-FixtureFile([string] $Root, [string] $RelativePath, [string] $Value = "fixture") {
  $path = Join-Path $Root $RelativePath
  New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
  Set-Content -LiteralPath $path -Value $Value -Encoding utf8
}

function New-Fixture([string] $Name) {
  $root = Join-Path $testRoot $Name
  Write-FixtureFile $root "VERSION" "1.2.3"
  foreach ($document in @("README.md", "README.zh-CN.md", "LICENSE", "NOTICE")) {
    Write-FixtureFile $root $document
  }
  Write-FixtureFile $root "packaging/placeholder"
  Copy-Item -LiteralPath $sourceScript -Destination (Join-Path $root "packaging")
  foreach ($inputPath in @(
    "src/Tripo.Revit.Mcp/bin/Release/net8.0/Tripo.Revit.Mcp.dll",
    "src/Tripo.Revit/bin/Release/net8.0-windows/2025/Tripo.Revit.dll",
    "src/Tripo.Revit/bin/Release/net8.0-windows/2025/Tripo.Revit.addin",
    "src/Tripo.Revit/bin/Release/net8.0-windows/2025/sidecar/Tripo.Revit.Mcp.dll",
    "src/Tripo.Revit/bin/Release/net8.0-windows/2026/Tripo.Revit.dll",
    "src/Tripo.Revit/bin/Release/net8.0-windows/2026/Tripo.Revit.addin",
    "src/Tripo.Revit/bin/Release/net8.0-windows/2026/sidecar/Tripo.Revit.Mcp.dll"
  )) {
    Write-FixtureFile $root $inputPath
  }
  return $root
}

function Invoke-Candidate([string] $Root, [hashtable] $Extra = @{}) {
  & (Join-Path $Root "packaging/New-ReleaseCandidate.ps1") -Version v1.2.3 @Extra | Out-Null
}

function Assert-Refused([scriptblock] $Action, [string] $Expected) {
  $failure = $null
  try { & $Action } catch { $failure = $_ }
  Assert-True ($null -ne $failure) "Expected refusal: $Expected"
  Assert-True ($failure.Exception.Message.Contains($Expected)) "Unexpected failure: $failure"
}

function Assert-NoArtifacts([string] $Root) {
  $directory = Join-Path $Root "artifacts"
  if (Test-Path -LiteralPath $directory) {
    Assert-True (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) "Failed build left candidate files."
  }
}

try {
  $root = New-Fixture "successful candidate with spaces"
  Invoke-Candidate $root
  $candidateName = "tripo-revit-v1.2.3"
  $archive = Join-Path $root "artifacts/$candidateName.zip"
  $checksum = "$archive.sha256"
  $candidate = Join-Path $root "artifacts/$candidateName"
  $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
  Assert-True ((Get-Content -LiteralPath $checksum -Raw).Trim() -ceq "$hash  $candidateName.zip") "Checksum does not match archive."
  $zip = [IO.Compression.ZipFile]::OpenRead($archive)
  try {
    $members = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($member in @("mcp/Tripo.Revit.Mcp.dll", "Addins/2025/Tripo.Revit.addin", "Addins/2025/TripoRevit/Tripo.Revit.dll", "Addins/2025/TripoRevit/sidecar/Tripo.Revit.Mcp.dll", "Addins/2026/Tripo.Revit.addin", "Addins/2026/TripoRevit/Tripo.Revit.dll", "Addins/2026/TripoRevit/sidecar/Tripo.Revit.Mcp.dll", "LICENSE", "NOTICE")) {
      Assert-True ($members -ccontains "$candidateName/$member") "Archive is missing $member."
    }
  } finally { $zip.Dispose() }
  Assert-Refused { Invoke-Candidate $root } "Candidate already exists"
  Assert-True ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $hash) "Existing ZIP changed."
  Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $root "artifacts") -Force).Count -eq 3) "Unexpected successful-build leftovers."

  foreach ($existing in @($candidateName, "$candidateName.zip", "$candidateName.zip.sha256")) {
    $root = New-Fixture "existing-$existing"
    Write-FixtureFile $root "artifacts/$existing" "preserve this evidence"
    Assert-Refused { Invoke-Candidate $root } "Candidate already exists"
    Assert-True ((Get-Content -LiteralPath (Join-Path $root "artifacts/$existing") -Raw).Trim() -ceq "preserve this evidence") "Existing candidate evidence changed."
  }

  $root = New-Fixture "locked"
  $lockPath = Join-Path $root "artifacts/$candidateName.lock"
  Write-FixtureFile $root "artifacts/$candidateName.lock" "another builder"
  Assert-Refused { Invoke-Candidate $root } ""
  Assert-True ((Get-Content -LiteralPath $lockPath -Raw).Trim() -ceq "another builder") "Another builder's lock changed."
  Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $root "artifacts") -Force).Count -eq 1) "Locked attempt wrote output."

  $root = New-Fixture "compression failure"
  function Compress-Archive {
    param($LiteralPath, $DestinationPath, $CompressionLevel)
    Set-Content -LiteralPath $DestinationPath -Value "partial ZIP"
    throw "injected compression failure"
  }
  try { Assert-Refused { Invoke-Candidate $root } "injected compression failure" }
  finally { Remove-Item Function:\Compress-Archive }
  Assert-NoArtifacts $root
  Invoke-Candidate $root

  $root = New-Fixture "late collision"
  $collisionPath = Join-Path $root "artifacts/$candidateName.zip"
  function Compress-Archive {
    param($LiteralPath, $DestinationPath, $CompressionLevel)
    Microsoft.PowerShell.Archive\Compress-Archive -LiteralPath $LiteralPath -DestinationPath $DestinationPath -CompressionLevel $CompressionLevel
    Set-Content -LiteralPath $collisionPath -Value "unowned late output"
  }
  try { Assert-Refused { Invoke-Candidate $root } "" }
  finally { Remove-Item Function:\Compress-Archive }
  Assert-True ((Get-Content -LiteralPath $collisionPath -Raw).Trim() -ceq "unowned late output") "Late collision output was overwritten or deleted."
  Assert-True (@(Get-ChildItem -LiteralPath (Join-Path $root "artifacts") -Force).Count -eq 1) "Late collision leaked owned outputs."

  $root = New-Fixture "invalid version"
  Write-FixtureFile $root "VERSION" "../outside"
  Assert-Refused { Invoke-Candidate $root } "canonical release version"
  Assert-NoArtifacts $root

  $root = New-Fixture "missing document"
  Remove-Item -LiteralPath (Join-Path $root "LICENSE")
  Assert-Refused { Invoke-Candidate $root } ""
  Assert-NoArtifacts $root
  foreach ($years in @(@(), @(2024), @(2026, 2026))) {
    $root = New-Fixture "invalid-years-$([Guid]::NewGuid().ToString('N'))"
    Assert-Refused { Invoke-Candidate $root @{ RevitYears = $years } } "distinct supported years"
    Assert-NoArtifacts $root
  }
  $root = New-Fixture "single host year"
  Invoke-Candidate $root @{ RevitYears = @(2026) }
  Assert-True (Test-Path -LiteralPath (Join-Path $root "artifacts/$candidateName/Addins/2026/Tripo.Revit.addin")) "Requested host year missing."
  Assert-True (-not (Test-Path -LiteralPath (Join-Path $root "artifacts/$candidateName/Addins/2025"))) "Unrequested host year packaged."

  Write-Output "Release candidate regression tests passed (synthetic files; no host or release acceptance)."
} finally {
  Remove-Item -LiteralPath $testRoot -Recurse -Force
}
