@echo off
setlocal
pushd "%~dp0processor"
if errorlevel 1 exit /b 1

where dotnet >nul 2>&1
if errorlevel 1 goto missing_dotnet
where node >nul 2>&1
if errorlevel 1 goto missing_node
where npm.cmd >nul 2>&1
if errorlevel 1 goto missing_node

rem Running beneath processor selects its exact global.json SDK.
dotnet --version
if errorlevel 1 goto failed
call npm.cmd --prefix web ci --ignore-scripts --no-fund --engine-strict
if errorlevel 1 goto failed
call npm.cmd --prefix web run build
if errorlevel 1 goto failed
call npm.cmd --prefix web audit --audit-level=low
if errorlevel 1 goto failed
dotnet restore src\Mpa.Processor.Desktop\Mpa.Processor.Desktop.csproj --locked-mode
if errorlevel 1 goto failed
dotnet build src\Mpa.Processor.Desktop\Mpa.Processor.Desktop.csproj --no-restore --configuration Release
if errorlevel 1 goto failed

echo Desktop build ready: "%~dp0processor\src\Mpa.Processor.Desktop\bin\Release\net10.0\MPA.Processor.exe"
set "MPA_DESKTOP_EXIT=0"
goto done

:missing_dotnet
>&2 echo Install the .NET SDK version pinned in processor\global.json and put dotnet on PATH.
set "MPA_DESKTOP_EXIT=1"
goto done

:missing_node
>&2 echo Install Node.js and npm compatible with processor\web\package.json and put them on PATH.
set "MPA_DESKTOP_EXIT=1"
goto done

:failed
set "MPA_DESKTOP_EXIT=%errorlevel%"
>&2 echo Desktop build stopped. The application was not started.

:done
popd
exit /b %MPA_DESKTOP_EXIT%
