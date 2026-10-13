<#
.SYNOPSIS
  Build the Source2.Compiler NuGet package (and its symbols) into artifacts/packages.

.DESCRIPTION
  Runs the test suite against the lowest ValveKeyValue the package supports, then packs with that
  version as the dependency's lower bound (vpkstuff's VRF pins 0.70.0.499; 0.71 gives the same
  output). Without -Release the version gets a dev suffix: dev.<yyyyMMdd>.<short sha>.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\pack.ps1
  powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -Release -SkipTests
#>
param(
  [switch]$Release,
  [switch]$SkipTests,
  [string]$ValveKeyValueVersion = "0.70.0.499",
  # An exact version (tools/publish-to-vpkstuff.ps1 passes 0.1.<commit count>); overrides -Release.
  [string]$Version = ""
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\packages"
$kv = "/p:Source2CompilerValveKeyValueVersion=$ValveKeyValueVersion"

if (-not $SkipTests) {
  dotnet test (Join-Path $root "tests\Source2.Compiler.Tests") -c Release $kv --nologo
  if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

$suffix = @()
if ($Version) {
  $suffix = @("/p:Version=$Version")
} elseif (-not $Release) {
  $sha = (git -C $root rev-parse --short HEAD).Trim()
  $suffix = @("/p:VersionSuffix=dev.$(Get-Date -Format yyyyMMdd).$sha")
}
New-Item -ItemType Directory -Force $out | Out-Null
dotnet pack (Join-Path $root "src\Source2.Compiler") -c Release -o $out $kv @suffix --nologo
if ($LASTEXITCODE -ne 0) { throw "pack failed" }
Get-ChildItem $out -Filter "Source2.Compiler.*" | Sort-Object LastWriteTime | Select-Object -Last 2 | ForEach-Object { $_.FullName }
