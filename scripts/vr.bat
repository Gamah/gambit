@echo off
rem Open gambit's editor in VR: sbox-dev.exe needs -vr on its own command line, and the switch
rem does not survive every route into the editor. Make a desktop SHORTCUT to this file rather
rem than copying it, so the project path below still resolves.
rem
rem Override either path from the environment if the search below guesses wrong:
rem   set SBOX_DEV=D:\somewhere\sbox-dev.exe
setlocal

set "PROJECT=%~1"
if "%PROJECT%"=="" set "PROJECT=%~dp0..\client\gambit.sbproj"

if not exist "%PROJECT%" (
	echo Project not found: %PROJECT%
	echo Pass it as an argument, or run a shortcut to this file instead of a copy of it.
	pause
	exit /b 1
)

if defined SBOX_DEV goto :launch

rem Steam's own install, then the library folders people actually use. Plain existence checks:
rem no .vdf parsing, nothing recursive.
for /f "tokens=2,*" %%a in ('reg query "HKCU\Software\Valve\Steam" /v SteamPath 2^>nul') do call :try "%%b"
call :try "%ProgramFiles(x86)%\Steam"
call :try "%ProgramFiles%\Steam"
for %%d in (C D E F G) do (
	call :try "%%d:\Steam"
	call :try "%%d:\SteamLibrary"
	call :try "%%d:\Games\Steam"
	call :try "%%d:\Games\SteamLibrary"
	call :try "%%d:\SteamGames"
)

if not defined SBOX_DEV (
	echo Could not find sbox-dev.exe.
	echo Steam ^> s^&box ^> Manage ^> Browse local files, then:
	echo   set SBOX_DEV=^<that folder^>\sbox-dev.exe
	echo and run this again.
	pause
	exit /b 1
)

:launch
echo Launching %SBOX_DEV%
echo   project %PROJECT%
start "" "%SBOX_DEV%" -project "%PROJECT%" -vr
exit /b 0

:try
if defined SBOX_DEV exit /b 0
if exist "%~1\steamapps\common\sbox\sbox-dev.exe" set "SBOX_DEV=%~1\steamapps\common\sbox\sbox-dev.exe"
exit /b 0
