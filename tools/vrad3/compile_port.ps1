<#
.SYNOPSIS
  Compile a community map port far enough to get a lighting job out of it.

.DESCRIPTION
  A ported map names materials nobody has the sources for, and resourcecompiler
  treats a missing material as fatal. So this alternates: compile the map, stub
  whatever it named, compile the stubs, try again. It stops when the map builds,
  when a round adds no new stubs (the failure is something else), or after
  -MaxRounds.

  The order matters and is the part that wastes an afternoon if you get it wrong:
  a stub must be COMPILED before the map build can see it. A stub sitting in the
  content tree is invisible and the error stays "referencing missing material".

.EXAMPLE
  compile_port.ps1 -Addon s2c_big -Map ze_ffvii_mako_reactor_v6_p
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Addon,
    [Parameter(Mandatory)] [string] $Map,
    [string] $Cs2 = "D:\Steam\steamapps\common\Counter-Strike Global Offensive",
    [int] $MaxRounds = 8
)

$rc = Join-Path $Cs2 "game\bin\win64\resourcecompiler.exe"
$game = Join-Path $Cs2 "game\csgo"
$content = Join-Path $Cs2 "content\csgo_addons\$Addon"
$mapSource = Join-Path $content "maps\$Map.vmap"
$stubber = Join-Path $PSScriptRoot "stub_missing_materials.py"
$log = Join-Path $env:TEMP "rc_$Addon.log"

if (-not (Test-Path $rc)) { throw "no resourcecompiler at $rc" }
if (-not (Test-Path $mapSource)) { throw "no map source at $mapSource" }

for ($round = 1; $round -le $MaxRounds; $round++) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $rc -nop4 -f -game $game -i $mapSource > $log 2>&1
    $code = $LASTEXITCODE
    Write-Output "round ${round}: exit=$code elapsed=$([int]$timer.Elapsed.TotalSeconds)s"
    if ($code -eq 0) {
        Write-Output "map built; lighting job is at game\csgo_addons\$Addon\_vrad3"
        return
    }

    $stubbed = & python $stubber $Addon $log
    Write-Output "  $stubbed"
    if ($stubbed -match ", 0 stub") {
        Write-Output "  no new stubs, so the failure is something else:"
        Select-String -Path $log -Pattern "RESOURCE COMPILE ERROR|FATAL|Error:" |
            Select-Object -Last 5 | ForEach-Object { "    " + $_.Line.Trim() }
        return
    }

    # The stubs only count once they are compiled resources.
    & $rc -nop4 -f -game $game -i (Join-Path $content "materials\*.vmat") -r > "$log.materials" 2>&1
    Write-Output "  materials: $((Select-String -Path "$log.materials" -Pattern '^ OK: ' | Select-Object -Last 1).Line)"
}
Write-Output "gave up after $MaxRounds rounds"
