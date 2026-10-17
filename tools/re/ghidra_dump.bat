@echo off
rem ghidra_dump.bat <substring> <outfile> -- DumpByString.java over visbuilder.dll in the cs2 project, headless.
rem Ghidra and its project are under S2C_GHIDRA_ROOT (default D:\tools); the scripts are the repo's.
if "%S2C_GHIDRA_ROOT%"=="" set S2C_GHIDRA_ROOT=D:\tools
"%S2C_GHIDRA_ROOT%\ghidra_12.1.3_PUBLIC\support\analyzeHeadless.bat" "%S2C_GHIDRA_ROOT%\ghidra_projects" cs2 -process visbuilder.dll -noanalysis -scriptPath "%~dp0..\ghidra_scripts" -postScript DumpByString.java %1 %2
