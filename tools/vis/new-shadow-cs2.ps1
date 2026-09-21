<#
.SYNOPSIS
A CS2 install that shares the real one's bytes but has its own gameinfo.gi.

.DESCRIPTION
The VisBuilder knobs live in a ResourceCompiler block in gameinfo.gi, so measuring
them means editing that file. Editing the real one is not on while the game is
running, and copying the install is not on either: game/csgo alone is 60 GB.

Everything here is metadata. Directories become junctions and files become
hardlinks, both of which point at the same bytes on the same volume and cost
nothing, and the ONE file that has to differ is copied for real. Nothing is
written to the real install, and nothing runs, so this is safe with CS2 open.

Resource compiler outputs still land in the real game/csgo_addons, which is
junctioned in: those are our own scratch addons and the point of the shadow is
the config, not the output.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File new-shadow-cs2.ps1
powershell -ExecutionPolicy Bypass -File new-shadow-cs2.ps1 -Force
#>
[CmdletBinding()]
param(
    [string] $Cs2 = 'D:\Steam\steamapps\common\Counter-Strike Global Offensive',
    [string] $Shadow = 'D:\cs2-shadow',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $Cs2 'game\csgo\gameinfo.gi'))) {
    throw "No CS2 install at $Cs2"
}
if (Test-Path $Shadow) {
    if (-not $Force) { throw "$Shadow already exists; pass -Force to rebuild it" }
    # Remove junctions without following them into the real install.
    Get-ChildItem $Shadow -Recurse -Force -Directory |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
        ForEach-Object { [IO.Directory]::Delete($_.FullName) }
    Remove-Item $Shadow -Recurse -Force
}

function Link-Dir([string] $Link, [string] $Target) {
    New-Item -ItemType Junction -Path $Link -Target $Target | Out-Null
}
function Link-File([string] $Link, [string] $Target) {
    New-Item -ItemType HardLink -Path $Link -Target $Target | Out-Null
}

$realGame = Join-Path $Cs2 'game'
$shadowGame = Join-Path $Shadow 'game'
New-Item -ItemType Directory -Path $shadowGame -Force | Out-Null

# Every mod dir beside csgo is shared verbatim. csgo itself is rebuilt entry by
# entry below, because its gameinfo.gi is the whole reason this exists.
foreach ($entry in Get-ChildItem $realGame -Force) {
    if ($entry.Name -eq 'csgo') { continue }
    $link = Join-Path $shadowGame $entry.Name
    if ($entry.PSIsContainer) { Link-Dir $link $entry.FullName } else { Link-File $link $entry.FullName }
}

$realCsgo = Join-Path $realGame 'csgo'
$shadowCsgo = Join-Path $shadowGame 'csgo'
New-Item -ItemType Directory -Path $shadowCsgo -Force | Out-Null

$copied = 0; $linkedDirs = 0; $linkedFiles = 0
foreach ($entry in Get-ChildItem $realCsgo -Force) {
    $link = Join-Path $shadowCsgo $entry.Name
    if ($entry.Name -eq 'gameinfo.gi') {
        Copy-Item $entry.FullName $link            # the one real file
        $copied++
    }
    elseif ($entry.PSIsContainer) { Link-Dir $link $entry.FullName; $linkedDirs++ }
    else { Link-File $link $entry.FullName; $linkedFiles++ }
}

# The content tree is read only as far as a compile is concerned.
$shadowContent = Join-Path $Shadow 'content'
New-Item -ItemType Directory -Path $shadowContent -Force | Out-Null
foreach ($entry in Get-ChildItem (Join-Path $Cs2 'content') -Force -Directory) {
    Link-Dir (Join-Path $shadowContent $entry.Name) $entry.FullName
}

$size = (Get-Item (Join-Path $shadowCsgo 'gameinfo.gi')).Length
Write-Host "shadow at $Shadow"
Write-Host "  game/csgo: $linkedDirs junctions, $linkedFiles hardlinks, $copied real file ($size bytes)"
Write-Host "  compile with: -game `"$shadowCsgo`""
