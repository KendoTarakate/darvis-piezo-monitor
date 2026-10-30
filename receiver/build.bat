@echo off
rem Builds PiezoReceiver.exe with the C# compiler that ships with Windows (.NET Framework 4).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:"%~dp0PiezoReceiver.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll "%~dp0PiezoReceiver.cs"
if errorlevel 1 (
  echo.
  echo Build FAILED.
  pause
  exit /b 1
)
echo Built %~dp0PiezoReceiver.exe
pause
