@echo off
setlocal
if "%1"=="" (
  set "CONFIG=Debug"
) else (
  set "CONFIG=%1"
)
if "%2"=="" (
  set "FRAMEWORK=net10.0-windows10.0.26100.0"
) else (
  set "FRAMEWORK=%2"
)
shift
shift
echo Running: dotnet run -c %CONFIG% -f %FRAMEWORK% %*
dotnet run -c %CONFIG% -f %FRAMEWORK% %*
endlocal
