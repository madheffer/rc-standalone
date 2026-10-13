$S = $PSScriptRoot
$env:PATH += ";C:\Program Files (x86)\Microsoft Visual Studio\Installer"
$vc = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
cmd /c "`"$vc`" >nul 2>&1 && cd /d `"$S`" && cl /nologo /O2 /EHsc /DUSE_SSE /DWIN32 /I hs atitest.cpp xcodec23.cpp /Fe:atitest.exe 2>&1" | Select-String -Pattern "error" | Select-Object -First 25
