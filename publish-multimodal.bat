@echo off
rem Publishes JevMultimodalApp as a single self-contained win-x64 exe into
rem   <project root>\publish-multimodal\JevMultimodalApp.exe
rem GPU (CUDA) is used automatically when a compatible NVIDIA GPU/driver is present on the
rem target machine, otherwise it falls back to CPU - no separate build is needed either way.

setlocal
cd /d "%~dp0"

dotnet publish src\JevMultimodalApp\JevMultimodalApp.csproj -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -o publish-multimodal

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo Publish failed.
    exit /b %ERRORLEVEL%
)

echo.
echo Published: %~dp0publish-multimodal\JevMultimodalApp.exe
endlocal
