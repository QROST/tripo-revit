[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $Tag,

  [Parameter(Mandatory = $true)]
  [string] $Repository,

  [string] $ExpectedEventCommit = "",

  [switch] $RequireReleaseAbsent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Tag = $Tag.Trim()
$Repository = $Repository.Trim()
$ExpectedEventCommit = $ExpectedEventCommit.Trim()
if ($Tag -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
  throw "Tag '$Tag' is not a canonical release tag."
}
if ($Repository -cnotmatch '^[0-9A-Za-z_.-]+/[0-9A-Za-z_.-]+$') {
  throw "Repository '$Repository' is not a canonical owner/name pair."
}
if ($ExpectedEventCommit -and
    $ExpectedEventCommit -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
  throw "The triggering event commit is not a canonical Git object ID."
}
if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
  throw "GH_TOKEN is required to verify the remote tag and release state."
}

function Invoke-GitText {
  param([Parameter(Mandatory = $true)][string[]] $Arguments)

  $lines = & git @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "git $($Arguments[0]) failed while verifying release identity."
  }

  return (($lines | Out-String).Trim())
}

& git show-ref --verify --quiet "refs/tags/$Tag"
if ($LASTEXITCODE -ne 0) {
  throw "Requested tag '$Tag' does not exist in the checkout."
}

$tagCommit = Invoke-GitText @("rev-list", "-n", "1", "$Tag^{commit}")
$tagRefSha = Invoke-GitText @("rev-parse", "refs/tags/$Tag")
$headCommit = Invoke-GitText @("rev-parse", "HEAD")
if ($tagCommit -cne $headCommit) {
  throw "Checked-out commit '$headCommit' does not match tag '$Tag' commit '$tagCommit'."
}

if ($ExpectedEventCommit) {
  $eventCommit = Invoke-GitText @(
    "rev-parse",
    "$ExpectedEventCommit^{commit}")
  if ($eventCommit -cne $headCommit) {
    throw "Tag-push event commit '$eventCommit' does not match checked-out commit '$headCommit'."
  }
}

$declaredVersion = (Get-Content -LiteralPath VERSION -Raw).Trim()
if ($Tag -cne "v$declaredVersion") {
  throw "Tag '$Tag' does not match VERSION '$declaredVersion'."
}

$headers = @{
  Accept = "application/vnd.github+json"
  Authorization = "Bearer $env:GH_TOKEN"
  "X-GitHub-Api-Version" = "2022-11-28"
}
$encodedTag = [Uri]::EscapeDataString($Tag)
$remoteRefResponse = Invoke-WebRequest `
  -Uri "https://api.github.com/repos/$Repository/git/ref/tags/$encodedTag" `
  -Headers $headers `
  -Method Get `
  -SkipHttpErrorCheck
if ([int]$remoteRefResponse.StatusCode -ne 200) {
  throw "Could not resolve remote tag ref '$Tag' (HTTP $([int]$remoteRefResponse.StatusCode))."
}
$remoteTagRefSha = ($remoteRefResponse.Content | ConvertFrom-Json).object.sha
if ($remoteTagRefSha -cne $tagRefSha) {
  throw "Remote tag ref '$Tag' does not match checked-out ref object '$tagRefSha'."
}
$remoteObject = ($remoteRefResponse.Content | ConvertFrom-Json).object
$peelDepth = 0
while ($remoteObject.type -ceq "tag") {
  if ($peelDepth -ge 8) {
    throw "Remote tag '$Tag' exceeds the bounded annotated-tag depth."
  }
  $peelDepth++
  $tagObjectResponse = Invoke-WebRequest `
    -Uri "https://api.github.com/repos/$Repository/git/tags/$($remoteObject.sha)" `
    -Headers $headers `
    -Method Get `
    -SkipHttpErrorCheck
  if ([int]$tagObjectResponse.StatusCode -ne 200) {
    throw "Could not peel remote tag '$Tag' (HTTP $([int]$tagObjectResponse.StatusCode))."
  }
  $remoteObject = ($tagObjectResponse.Content | ConvertFrom-Json).object
}
if ($remoteObject.type -cne "commit" -or
    $remoteObject.sha -cnotmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$' -or
    $remoteObject.sha -cne $headCommit) {
  throw "Remote tag '$Tag' does not resolve to checked-out commit '$headCommit'."
}

if ($RequireReleaseAbsent) {
  $releaseResponse = Invoke-WebRequest `
    -Uri "https://api.github.com/repos/$Repository/releases/tags/$encodedTag" `
    -Headers $headers `
    -Method Get `
    -SkipHttpErrorCheck
  if ([int]$releaseResponse.StatusCode -eq 200) {
    throw "Release '$Tag' already exists. Existing releases and assets are immutable."
  }
  if ([int]$releaseResponse.StatusCode -ne 404) {
    throw "Could not prove that release '$Tag' is absent (HTTP $([int]$releaseResponse.StatusCode))."
  }
}

Write-Output "Verified immutable release identity $Tag ($tagRefSha) -> $headCommit."
