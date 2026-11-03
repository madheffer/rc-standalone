<#
.SYNOPSIS
  Release this commit of main as Source2.Compiler 0.1.<commit count> into the release feed on the box.

.DESCRIPTION
  The one command after a merge to main. Consumers (vpkstuff's Dependencies tab and its
  deploy/dep-release.sh) read the feed; nothing here edits a consumer's repository.

    1. refuses unless HEAD is main exactly as the box has it, with a clean tree, so the version
       (the commit count) and the sha mean the same thing to every clone;
    2. refuses a version the feed already has: a release never changes;
    3. packs (tools/pack.ps1: tests first unless -SkipTests) with package validation against the
       previous release, so a breaking change to the public API stops the release unless
       -AllowBreaking, which records the diagnostics in release.json;
    4. writes release.json: the files and their SHA-256, the package's dependency floors, the API
       verdict and the release notes (the lines CHANGELOG.md gained since the previous release);
    5. uploads the package, the VRF adapter (src/Source2.Compiler.Vrf at this commit, as a tar)
       and release.json, then publishes them with tools/feed_index.py on the box, which checks the
       hashes and rebuilds index.json atomically;
    6. tags the commit v<version> and pushes the tag to the box.

  -DryRun does 1-4 and prints release.json, uploading nothing (and only warns when HEAD is not
  box/main, so the script can be tried on a branch).

  -Commit <sha> -Import <nupkg> publishes a package that was already built from an earlier commit of
  main (one a consumer already runs, say), instead of packing: the nuspec's repository commit must
  be that sha, and the version is that commit's count. Packing always builds the checkout, so a
  -Commit other than HEAD needs -Import.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\release.ps1
  powershell -ExecutionPolicy Bypass -File tools\release.ps1 -SkipTests -DryRun
  powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Commit c156a79 -Import Source2.Compiler.0.1.127.nupkg
#>
param(
  [switch]$SkipTests,
  [switch]$AllowBreaking,
  [switch]$DryRun,
  [string]$Commit = "HEAD",
  [string]$Import = "",
  [string]$Box = "dev@192.168.20.32",
  [string]$Key = (Join-Path $HOME ".ssh\vpkworker"),
  [string]$Feed = "/srv/storage/data500/s2c-feed"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$repoUrl = "dev@192.168.20.32:repos/rc-standalone.git"
function Invoke-Native([string]$what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" } }
function Remote([string]$cmd) { & ssh -i $Key -o BatchMode=yes $Box $cmd; Invoke-Native "ssh: $cmd" }
function Write-Utf8([string]$path, [string]$text) { [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false)) }

# 1. a commit of main as the box has it; packing needs it checked out and clean
& git -C $root fetch --quiet box; Invoke-Native "git fetch box"
$sha = (git -C $root rev-parse "$Commit^{commit}").Trim(); Invoke-Native "resolving $Commit"
$head = (git -C $root rev-parse HEAD).Trim()
$boxMain = (git -C $root rev-parse box/main).Trim()
if ($Import) {
  if (-not (Test-Path $Import)) { throw "$Import does not exist" }
  & git -C $root merge-base --is-ancestor $sha $boxMain
  if ($LASTEXITCODE -ne 0) { throw "$($sha.Substring(0,9)) is not on box/main: only main's commits are released" }
} else {
  if ($sha -ne $head) { throw "packing builds the checkout: check out $($sha.Substring(0,9)), or pass -Import with a package built from it" }
  if ((git -C $root status --porcelain) -ne $null) { throw "the tree has uncommitted changes; a release is a commit" }
  if ($sha -ne $boxMain) {
    $why = "HEAD ($($sha.Substring(0,9))) is not box/main ($($boxMain.Substring(0,9))): release main after it is pushed"
    if (-not $DryRun) { throw $why }
    Write-Warning "$why (dry run, so going on)"
  }
}
$version = "0.1.$((git -C $root rev-list --count $sha).Trim())"
$date = (git -C $root show -s --format=%cI $sha).Trim()
Write-Host "== Source2.Compiler $version ($($sha.Substring(0,9)))" -ForegroundColor Cyan

# 2. the feed's view: refuse a published version, find the previous release
$indexJson = & ssh -i $Key -o BatchMode=yes $Box "cat '$Feed/index.json' 2>/dev/null || true"
$releases = @()
if ($indexJson) { $releases = @(($indexJson | Out-String | ConvertFrom-Json).releases) }
if ($releases | Where-Object { $_.version -eq $version }) { throw "$version is already in the feed; a release never changes" }
function VersionKey([string]$v) { [version](($v -split '-')[0]) }
$previous = $releases | Where-Object { (VersionKey $_.version) -lt (VersionKey $version) } |
  Sort-Object { VersionKey $_.version } -Descending | Select-Object -First 1

$work = Join-Path ([IO.Path]::GetTempPath()) "s2c-release-$version"
if (Test-Path $work) { Remove-Item -Recurse -Force $work -Confirm:$false }
New-Item -ItemType Directory -Force $work | Out-Null

# 3. pack, with the previous release as the API baseline
$baseline = ""
if ($previous -and -not $Import) {
  $baseline = Join-Path $work "baseline.nupkg"
  & scp -q -i $Key -o BatchMode=yes "${Box}:$Feed/$($previous.path)/Source2.Compiler.$($previous.version).nupkg" $baseline
  Invoke-Native "downloading the $($previous.version) baseline"
  Write-Host "API baseline: $($previous.version) ($($previous.sha.Substring(0,9)))"
}
$breaking = $false; $diagnostics = @()
if ($Import) {
  # An imported package was built earlier; its API is not re-validated, only its provenance.
  Copy-Item $Import (Join-Path $work "Source2.Compiler.$version.nupkg")
} else {
  $pack = @("-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "pack.ps1"), "-Version", $version)
  if ($SkipTests) { $pack += "-SkipTests" }
  # Windows PowerShell turns a native command's redirected stderr into a terminating error under
  # 'Stop', which would end the script before the CP diagnostics are read.
  $ErrorActionPreference = 'Continue'
  $log = & powershell @($pack + $(if ($baseline) { @("-BaselinePackage", $baseline) } else { @() })) 2>&1 | ForEach-Object { "$_" }
  $packExit = $LASTEXITCODE
  $ErrorActionPreference = 'Stop'
  $log | Write-Host
  if ($packExit -ne 0) {
    $diagnostics = @($log | Select-String -Pattern 'error (CP\d{4}):' | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
    if (-not $baseline -or $diagnostics.Count -eq 0) { throw "pack failed" }
    $breaking = $true
    Write-Host "Breaking API changes against $($previous.version):" -ForegroundColor Yellow
    $diagnostics | ForEach-Object { Write-Host "  $_" }
    if (-not $AllowBreaking) { throw "the public API breaks $($previous.version)'s; re-run with -AllowBreaking to release it as breaking" }
    & powershell @($pack + @("-SkipTests")); Invoke-Native "pack (without validation)"
  }
  $nupkg = Join-Path $root "artifacts\packages\Source2.Compiler.$version.nupkg"
  if (-not (Test-Path $nupkg)) { throw "$nupkg was not produced" }
  Copy-Item $nupkg $work
}

# 4. release.json
& git -C $root archive --format=tar -o (Join-Path $work "adapter.tar") $sha src/Source2.Compiler.Vrf; Invoke-Native "git archive"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $work "Source2.Compiler.$version.nupkg"))
try {
  $entry = $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.FullName -like '*.nuspec' } | Select-Object -First 1
  $reader = New-Object IO.StreamReader($entry.Open()); [xml]$nuspec = $reader.ReadToEnd(); $reader.Close()
} finally { $zip.Dispose() }
$built = $nuspec.SelectSingleNode("//*[local-name()='repository']")
if (-not $built -or $built.commit -ne $sha) { throw "the package was built from '$($built.commit)', not $sha" }
$packed = $nuspec.SelectSingleNode("//*[local-name()='version']").InnerText
if ($packed -ne $version) { throw "the package says version $packed, but $($sha.Substring(0,9)) is $version" }
$deps = @($nuspec.SelectNodes("//*[local-name()='dependency']") | ForEach-Object {
  [ordered]@{ id = $_.id; version = ($_.version -replace '[\[\(\]\)\s]', '' -split ',')[0] } })
$notes = ""
if ($previous) {
  & git -C $root cat-file -e "$($previous.sha)^{commit}" 2>$null
  if ($LASTEXITCODE -eq 0) {
    $notes = (@(git -C $root diff --no-color "$($previous.sha)" $sha -- CHANGELOG.md) |
      Where-Object { $_ -match '^\+' -and $_ -notmatch '^\+\+\+' } | ForEach-Object { $_.Substring(1) }) -join "`n"
  }
}
$files = @(Get-ChildItem $work -File | Where-Object { $_.Name -ne "baseline.nupkg" } | Sort-Object Name | ForEach-Object {
  [ordered]@{ name = $_.Name; sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
$release = [ordered]@{
  schema = 1
  package = "Source2.Compiler"
  version = $version
  sha = $sha
  repo = $repoUrl
  date = $date
  files = $files
  nupkg = "Source2.Compiler.$version.nupkg"
  adapter = [ordered]@{ tar = "adapter.tar"; path = "src/Source2.Compiler.Vrf" }
  dependencies = $deps
  api = [ordered]@{ baseline = $(if ($previous) { $previous.version } else { $null }); validated = [bool]($baseline); breaking = $breaking; diagnostics = $diagnostics }
  notes = $notes
}
if (Test-Path (Join-Path $work "baseline.nupkg")) { Remove-Item (Join-Path $work "baseline.nupkg") -Confirm:$false }
Write-Utf8 (Join-Path $work "release.json") ($release | ConvertTo-Json -Depth 6)
if ($DryRun) {
  Get-Content (Join-Path $work "release.json")
  Write-Host "dry run: nothing uploaded ($work)" -ForegroundColor Yellow
  return
}

# 5. upload, then publish on the box (hash check, atomic index)
$incoming = "$Feed/.incoming-$version-$PID"
Remote "mkdir -p '$incoming'"
& scp -q -i $Key -o BatchMode=yes (Join-Path $work "*") (Join-Path $PSScriptRoot "feed_index.py") "${Box}:$incoming/"; Invoke-Native "upload"
Remote "mv '$incoming/feed_index.py' '$Feed/.feed_index.py' && python3 '$Feed/.feed_index.py' '$Feed' --add '$incoming'"

# 6. tag
& git -C $root tag -a "v$version" -m "Source2.Compiler $version" $sha; Invoke-Native "git tag"
& git -C $root push --quiet box "v$version"; Invoke-Native "git push tag"
Remove-Item -Recurse -Force $work -Confirm:$false
Write-Host "released Source2.Compiler $version ($($sha.Substring(0,9)))$(if ($breaking) { ' as BREAKING' })" -ForegroundColor Green
