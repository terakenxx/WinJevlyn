@echo off
rem Publishes WinJevlyn as a single self-contained win-x64 exe into
rem   <project root>\publish\WinJevlyn.exe
rem so the output is easy to find for distribution (instead of the deep
rem bin\Release\net8.0-windows\win-x64\publish\ path that "dotnet publish" uses by default).

setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-with-spinner.ps1 ^
  -Message "Publishing WinJevlyn (this can take a few minutes)" ^
  -Command dotnet ^
  -Arguments publish,src\WinJevlyn\WinJevlyn.csproj,-c,Release,-r,win-x64,--self-contained,true,-p:PublishSingleFile=true,-p:IncludeNativeLibrariesForSelfExtract=true,-p:EnableCompressionInSingleFile=true,-p:DebugType=None,-o,publish

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Publish failed.
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Published: %~dp0publish\WinJevlyn.exe
pause
endlocal
