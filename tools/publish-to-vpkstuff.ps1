<#
.SYNOPSIS
  Publish this commit's Source2.Compiler package into vpkstuff's folder feed, on a new vpkstuff branch.

.DESCRIPTION
  The one command after a merge to main (docs/CONSUMING.md, docs/VPKSTUFF_PACKAGE_HANDOFF.md):

    1. refuses a dirty tree: the package must be a commit;
    2. packs 0.1.<commit count> (tools/pack.ps1: tests first, against ValveKeyValue 0.70.0.499);
    3. adds a vpkstuff worktree on a new branch chore/compiler-<version> off box/master;
    4. puts the .nupkg and .snupkg in dotnet/packages/ (vpkstuff's folder feed), removing the old ones;
    5. when vpkstuff consumes the package (its dotnet/Directory.Packages.props pins Source2.Compiler),
       bumps that pin and Source2CompilerPackageVersion in dotnet/vendor/Directory.Build.props,
       refreshes the vendored VRF adapter with deploy/sync-source2-compiler.py, and runs vpkstuff's
       compiler gate (CompilerVrfCompatTests and the anim set);
    6. commits on that branch. It does not push unless -Push; the branch then goes through
       vpkstuff's normal staging and in-game check.

  Before vpkstuff has switched to the package, steps 5's edits are skipped and the branch only
  carries the package, for the switch to be built on (the hand-off document says how).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish-to-vpkstuff.ps1
  powershell -ExecutionPolicy Bypass -File tools\publish-to-vpkstuff.ps1 -SkipTests -NoGate
#>
param(
  [string]$Vpkstuff = "D:\_cursor projects\vpkstuff",
  [string]$Base = "box/master",
  [string]$Worktree = "",
  [switch]$SkipTests,
  [switch]$NoGate,
  [switch]$Push
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Invoke-Git([string]$dir) { & git -C $dir @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed in $dir" } }
$identity = @("-c", "user.email=madheffer@users.noreply.github.com", "-c", "user.name=embattled")

# 1. a clean tree: the package is exactly this commit
if ((git -C $root status --porcelain) -ne $null) { throw "the source2-compiler tree has uncommitted changes; commit them first" }
$count = (git -C $root rev-list --count HEAD).Trim()
$sha = (git -C $root rev-parse --short HEAD).Trim()
$version = "0.1.$count"
Write-Host "== Source2.Compiler $version ($sha)" -ForegroundColor Cyan

# 2. pack
$pack = @("-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "pack.ps1"), "-Version", $version)
if ($SkipTests) { $pack += "-SkipTests" }
& powershell @pack
if ($LASTEXITCODE -ne 0) { throw "pack failed" }
$nupkg = Join-Path $root "artifacts\packages\Source2.Compiler.$version.nupkg"
$snupkg = Join-Path $root "artifacts\packages\Source2.Compiler.$version.snupkg"
if (-not (Test-Path $nupkg)) { throw "$nupkg was not produced" }

# 3. a vpkstuff worktree on a new branch
$branch = "chore/compiler-$version"
if (-not $Worktree) { $Worktree = Join-Path (Split-Path $Vpkstuff -Parent) "vpkstuff-wt-compiler-$version" }
Invoke-Git $Vpkstuff fetch --quiet box
Invoke-Git $Vpkstuff worktree add --quiet -b $branch $Worktree $Base
Write-Host "worktree $Worktree on $branch (from $Base)"

# 4. the folder feed
$feed = Join-Path $Worktree "dotnet\packages"
New-Item -ItemType Directory -Force $feed | Out-Null
Get-ChildItem $feed -Filter "Source2.Compiler.*" -ErrorAction SilentlyContinue | Remove-Item -Force -Confirm:$false
Copy-Item $nupkg, $snupkg $feed
# the hand-off document rides along, so vpkstuff's copy always matches the package
$handoff = Join-Path $Worktree "docs\SOURCE2_COMPILER_PACKAGE.md"
New-Item -ItemType Directory -Force (Split-Path $handoff) | Out-Null
Copy-Item (Join-Path $root "docs\VPKSTUFF_PACKAGE_HANDOFF.md") $handoff

# 5. pins, adapter, gate: only once vpkstuff consumes the package
$cpm = Join-Path $Worktree "dotnet\Directory.Packages.props"
$vendorProps = Join-Path $Worktree "dotnet\vendor\Directory.Build.props"
$switched = (Select-String -Path $cpm -Pattern 'PackageVersion Include="Source2.Compiler"' -Quiet)
if ($switched) {
  (Get-Content $cpm -Raw) -replace '(PackageVersion Include="Source2\.Compiler" Version=")[^"]*(")', "`${1}$version`${2}" |
    Set-Content $cpm -NoNewline -Encoding utf8
  if (Test-Path $vendorProps) {
    (Get-Content $vendorProps -Raw) -replace '(<Source2CompilerPackageVersion>)[^<]*(</Source2CompilerPackageVersion>)', "`${1}$version`${2}" |
      Set-Content $vendorProps -NoNewline -Encoding utf8
  }
  $sync = Join-Path $Worktree "deploy\sync-source2-compiler.py"
  if (Test-Path $sync) {
    Push-Location $Worktree
    try { python $sync --from $root; if ($LASTEXITCODE -ne 0) { throw "sync-source2-compiler.py failed" } } finally { Pop-Location }
  }
  if (-not $NoGate) {
    $tests = Join-Path $Worktree "dotnet\VpkStuff.Tests"
    dotnet test $tests --nologo --filter "FullyQualifiedName~CompilerVrfCompatTests|FullyQualifiedName~AnimSet|FullyQualifiedName~AnimClip|FullyQualifiedName~ViewmodelAnimSet|FullyQualifiedName~Compiler"
    if ($LASTEXITCODE -ne 0) { throw "vpkstuff's compiler gate failed on $branch; the worktree is left for a look" }
  }
} else {
  Write-Warning "vpkstuff does not consume the package yet (no Source2.Compiler pin in dotnet/Directory.Packages.props): the branch only carries the package. See docs/VPKSTUFF_PACKAGE_HANDOFF.md."
}

# 6. commit
Invoke-Git $Worktree add -A dotnet/packages docs/SOURCE2_COMPILER_PACKAGE.md
if ($switched) { Invoke-Git $Worktree add -A dotnet/Directory.Packages.props dotnet/vendor }
$message = @"
chore(compiler): Source2.Compiler $version

From source2-compiler $sha (box: repos/rc-standalone.git, main), packed by tools/publish-to-vpkstuff.ps1
into the dotnet/packages folder feed.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
"@
$tmp = New-TemporaryFile
Set-Content $tmp $message -Encoding utf8
& git -C $Worktree @identity commit -q -F $tmp
Remove-Item $tmp
if ($LASTEXITCODE -ne 0) { throw "commit failed" }
if ($Push) { Invoke-Git $Worktree push --quiet box $branch }
Write-Host "done: $branch in $Worktree$(if ($Push) { ', pushed to box' } else { ' (not pushed)' })" -ForegroundColor Green
