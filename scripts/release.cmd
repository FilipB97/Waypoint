@echo off
REM Dwuklikowy wrapper na release.ps1 — pyta o wersje i czy publikowac.
setlocal
set "VER="
set "PUB="
set /p VER=Podaj wersje (np. 1.0.0), Enter = z csproj:
set /p PUB=Opublikowac release na GitHub? wpisz t = tak (Enter = tylko build):

set "ARGS="
if not "%VER%"=="" set "ARGS=-Version %VER%"
if /I "%PUB%"=="t" set "ARGS=%ARGS% -Publish"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %ARGS%
echo.
pause
