@echo off
rem ------------------------------------------------------------------
rem VoxLeap Personal build script. ASCII only (Windows cmd code page safe).
rem Uses the C# 5 compiler that ships with Windows (.NET Framework 4.x),
rem so nothing needs to be installed.
rem Output: %LOCALAPPDATA%\VoxLeap\VoxLeap.exe
rem ------------------------------------------------------------------
setlocal
set CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo BUILD FAILED: .NET Framework C# compiler not found
    exit /b 1
)
set OUTDIR=%LOCALAPPDATA%\VoxLeap
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
if not exist "%OUTDIR%\settings.json" copy /Y "%~dp0settings.template.json" "%OUTDIR%\settings.json" >nul

"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ ^
  /win32icon:"%~dp0voxleap.ico" ^
  /out:"%OUTDIR%\VoxLeap.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll ^
  "%~dp0App.cs" "%~dp0VoxleapCore.cs" "%~dp0SettingsCore.cs" "%~dp0SettingsForm.cs"
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
echo BUILD OK: %OUTDIR%\VoxLeap.exe
