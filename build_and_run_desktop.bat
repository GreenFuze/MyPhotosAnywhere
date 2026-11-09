@echo off
setlocal
call "%~dp0build_desktop.bat"
if errorlevel 1 exit /b %errorlevel%

rem Keep the console attached for startup errors and forward native app options.
dotnet "%~dp0processor\src\Mpa.Processor.Desktop\bin\Release\net10.0\MPA.Processor.dll" %*
exit /b %errorlevel%
