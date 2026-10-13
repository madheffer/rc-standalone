# Builds prebuilt/win-x64/bc7e_s2c_amd.dll and bc7e_s2c_intel.dll.
# Needs ispc 1.24 (ISPC=path, else ispc on PATH) and Visual Studio 2022's C++ tools.
# The linux-x64 libraries come from build.sh on a Linux machine.
$ErrorActionPreference = "Stop"
$S = $PSScriptRoot
$ispc = if ($env:ISPC) { $env:ISPC } else { "ispc" }
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
$vc = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
$vcvars = Join-Path $vc "VC\Auxiliary\Build\vcvars64.bat"
$obj = Join-Path $S "obj"; New-Item -ItemType Directory -Force $obj | Out-Null
$out = Join-Path $S "prebuilt\win-x64"; New-Item -ItemType Directory -Force $out | Out-Null
python (Join-Path $S "prepare.py") --out $obj
foreach ($v in @(@("amd", ""), @("intel", "-DBC7E_RCP_INTEL"))) {
  $name = $v[0]
  $args2 = @("-O2", (Join-Path $obj "bc7e_rcp.ispc"), "-o", (Join-Path $obj "bc7e_$name.obj"), "-h", (Join-Path $obj "bc7e_ispc.h"),
             "--target=avx2-i32x4", "--opt=fast-math", "--opt=disable-assertions")
  if ($v[1]) { $args2 += $v[1] }
  & $ispc @args2
  if ($LASTEXITCODE -ne 0) { throw "ispc failed" }
  cmd /c "`"$vcvars`" >nul 2>&1 && cl /nologo /O2 /EHsc /LD /I `"$obj`" `"$S\bc7e_s2c.cpp`" `"$obj\bc7e_$name.obj`" /Fo`"$obj\\`" /Fe`"$out\bc7e_s2c_$name.dll`""
  if ($LASTEXITCODE -ne 0) { throw "cl failed" }
}
Get-ChildItem $out
