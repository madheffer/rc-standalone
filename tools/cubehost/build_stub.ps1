# Builds the stub steam_api64.dll (steam_api_stub.c) with MSVC into cubehost's output folder.
param([string]$Out = "$PSScriptRoot\out\bin\win64")
$vcvars = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat" | Select-Object -First 1
if (-not $vcvars) { throw "no MSVC vcvars64.bat" }
New-Item -ItemType Directory -Force $Out | Out-Null
$obj = Join-Path $env:TEMP "cubehost_stub"
New-Item -ItemType Directory -Force $obj | Out-Null
cmd /c "`"$($vcvars.FullName)`" >nul && cl /nologo /O2 /LD /Fo`"$obj\\`" `"$PSScriptRoot\steam_api_stub.c`" /Fe`"$Out\steam_api64.dll`" /link /NOLOGO"
if ($LASTEXITCODE -ne 0) { throw "cl failed" }
