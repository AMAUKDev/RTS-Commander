@echo off
REM Project check: compile the Ground Control (RTS) mod. Build errors and warnings are the only
REM automated gate this repo has (its self-checks run inside the game at plugin load).
setlocal
if "%NUCLEAR_OPTION_DIR%"=="" set "NUCLEAR_OPTION_DIR=I:\SteamLibrary\steamapps\common\Nuclear Option"
dotnet build "%~dp0..\GroundControlRts.csproj" -c Release -nologo -v q
exit /b %ERRORLEVEL%
