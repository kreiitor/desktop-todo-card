@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [error] csc.exe not found. Install .NET Framework 4.x.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /codepage:65001 /out:TodoWidget.exe /win32manifest:app.manifest /win32icon:tray.ico /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll TodoWidget.cs
if errorlevel 1 (
  echo [error] build failed
  exit /b 1
)
echo Built TodoWidget.exe
