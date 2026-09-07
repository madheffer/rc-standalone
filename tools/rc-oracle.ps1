<#
.SYNOPSIS
  Re-derive the Source 2 container ground truth from CS2's own resourcecompiler.

.DESCRIPTION
  Source2ContainerAuthor (src/Source2.Compiler/Containers) authors compiled
  KV3-resource containers from scratch. Its per-type facts — resource version,
  block set, RED2 compiler identities + version fingerprints — are dumped from
  the REAL resourcecompiler.exe output, not guessed. When a CS2 update lands
  (especially one that touches the tools), run this script: it compiles small
  probe sources through the local resourcecompiler and prints the facts to
  compare against SpecByExtension. A drifted fingerprint means Valve bumped a
  compiler version; update the spec table (and re-bake tests/Source2.Compiler.Tests/RcReference/ by
  copying the freshly compiled probes) in the same change.

  Requires the CS2 Workshop Tools (the content/ tree next to game/).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\rc-oracle.ps1
#>
[CmdletBinding()]
param(
  [string]$Cs2Root = "D:\Steam\steamapps\common\Counter-Strike Global Offensive",
  [string]$Addon = "s2c_rc_probe"
)
$ErrorActionPreference = 'Stop'

$rc = Join-Path $Cs2Root "game\bin\win64\resourcecompiler.exe"
$ri = Join-Path $Cs2Root "game\bin\win64\resourceinfo.exe"
if (-not (Test-Path $rc)) { throw "resourcecompiler.exe not found under $Cs2Root (Workshop Tools installed?)" }

$content = Join-Path $Cs2Root "content\csgo_addons\$Addon"
$game = Join-Path $Cs2Root "game\csgo_addons\$Addon"
$repoRoot = Split-Path $PSScriptRoot -Parent
$reference = Join-Path $repoRoot "tests\Source2.Compiler.Tests\RcReference"

# Probe sources: prefer the committed fixtures (so the recompiled output is
# comparable against tests/Source2.Compiler.Tests/RcReference/*_c), fall back to nothing gracefully.
$probes = @(
  @{ Src = "rc_probe.vsndevts";  Folder = "soundevents" },
  @{ Src = "rc_probe.vdata";     Folder = "scripts" },
  # Integer/array typing oracle: every discriminating case for how RC widths
  # its KV3 integers (singleton 0/1, int32 boundaries, typed-array promotion).
  # Source2ContainerAuthor.NarrowIntegers mirrors it; the parity test compares
  # the decoded tree leaf by leaf, so a rule change here fails loudly.
  @{ Src = "rc_probe_ints.vdata"; Folder = "scripts" },
  @{ Src = "rc_probe_refs.vpcf"; Folder = "particles" }
)

foreach ($p in $probes) {
  $fixture = Join-Path $reference $p.Src
  if (-not (Test-Path $fixture)) { Write-Warning "fixture missing: $fixture — skipping"; continue }
  $dstDir = Join-Path $content $p.Folder
  New-Item -ItemType Directory -Force $dstDir | Out-Null
  Copy-Item $fixture $dstDir -Force
  $srcPath = Join-Path $dstDir $p.Src
  Write-Host "== compiling $($p.Src)" -ForegroundColor Cyan
  & $rc -nop4 -f -i $srcPath | Select-Object -Last 3
}

Write-Host ""
Write-Host "== compiled container facts (compare with Source2ContainerAuthor.SpecByExtension)" -ForegroundColor Green
foreach ($p in $probes) {
  $outName = $p.Src + "_c"
  $outPath = Join-Path (Join-Path $game $p.Folder) $outName
  if (-not (Test-Path $outPath)) { continue }
  $b = [IO.File]::ReadAllBytes($outPath)
  $blockCount = [BitConverter]::ToUInt32($b, 12)
  $blocks = for ($i = 0; $i -lt $blockCount; $i++) { [Text.Encoding]::ASCII.GetString($b, 16 + $i * 12, 4) }
  Write-Host ("{0}: resVer={1} blocks=[{2}]" -f $outName, [BitConverter]::ToUInt16($b, 6), ($blocks -join ' '))
  $out = & $ri -i $outPath -b RED2 2>$null
  $start = [Array]::FindIndex($out, [Predicate[string]]{ param($l) $l -match 'm_SpecialDependencies' })
  if ($start -ge 0) {
    $end = [Array]::FindIndex($out, $start, [Predicate[string]]{ param($l) $l -match 'm_SpecialInputDependencies' })
    $out[$start..($end - 1)] | Where-Object { $_ -match 'm_String|m_CompilerIdentifier|m_nFingerprint' }
  }
  Write-Host ""
}
Write-Host "If any identity/fingerprint above differs from SpecByExtension: update the table,"
Write-Host "copy the freshly compiled *_c probes over tests\Source2.Compiler.Tests\RcReference\, and re-run"
Write-Host "Source2ContainerAuthorTests (they compare our authored output against these files)."
