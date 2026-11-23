@echo off
setlocal
cd /d "%~dp0"
echo ==== MouseIA: compilando ====
where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo O .NET 8 SDK nao esta instalado.
  echo Instale com:  winget install Microsoft.DotNet.SDK.8
  echo Depois rode este arquivo de novo.
  pause
  exit /b 1
)
dotnet publish src\MouseIA\MouseIA.csproj -c Release -o publish
if errorlevel 1 (
  echo.
  echo A compilacao falhou. Veja as mensagens acima.
  pause
  exit /b 1
)
set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ISCC%" (
  "%ISCC%" installer\MouseIA.iss
  echo.
  echo Pronto! Instalador em: installer\Output\
) else (
  echo.
  echo Pronto! O MouseIA.exe portatil esta em: publish\
  echo Para gerar o instalador, instale o Inno Setup:  winget install JRSoftware.InnoSetup
)
pause
