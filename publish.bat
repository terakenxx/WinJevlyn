@echo off
rem Publishes WinJevlyn as a single self-contained win-x64 exe into
rem   <project root>\publish\WinJevlyn.exe
rem so the output is easy to find for distribution (instead of the deep
rem bin\Release\net8.0-windows\win-x64\publish\ path that "dotnet publish" uses by default).

setlocal
cd /d "%~dp0"

dotnet publish src\WinJevlyn\WinJevlyn.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -o publish

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Publish failed.
    exit /b %ERRORLEVEL%
)

echo.
echo Published: %~dp0publish\WinJevlyn.exe
endlocal
