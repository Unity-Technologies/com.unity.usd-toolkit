@echo off
REM ---------------------------------------------------------------------------------------------
REM Launch the Isaac Sim GUI with the Unity USD Live Sync extension enabled.
REM
REM The extension source stays here in the Unity project (single source of truth — nothing is copied
REM into the Isaac Sim install); --ext-folder just adds this folder to Kit's extension search path.
REM UNITY_USD_BASE_STAGE pre-fills the baseline path in the panel.
REM
REM Override the install location with:  set ISAACSIM_PATH=D:\path\to\isaacsim
REM ---------------------------------------------------------------------------------------------
setlocal

REM No ISAACSIM_PATH: probe the conventional install locations. Each line is parsed fresh, so the
REM "still empty" guard sees the previous line's assignment (a for-loop here would not).
if "%ISAACSIM_PATH%"=="" if exist "C:\isaacsim\isaac-sim.bat" set ISAACSIM_PATH=C:\isaacsim
if "%ISAACSIM_PATH%"=="" if exist "%USERPROFILE%\isaacsim\isaac-sim.bat" set ISAACSIM_PATH=%USERPROFILE%\isaacsim

if "%ISAACSIM_PATH%"=="" (
    echo [run_isaac_sim_with_livesync] Isaac Sim not found in the conventional locations
    echo [run_isaac_sim_with_livesync]   C:\isaacsim
    echo [run_isaac_sim_with_livesync]   %USERPROFILE%\isaacsim
    echo [run_isaac_sim_with_livesync] Set ISAACSIM_PATH to your install root and retry:
    echo [run_isaac_sim_with_livesync]   set ISAACSIM_PATH=D:\path\to\isaacsim
    exit /b 1
)

if not exist "%ISAACSIM_PATH%\isaac-sim.bat" (
    echo [run_isaac_sim_with_livesync] No isaac-sim.bat under "%ISAACSIM_PATH%".
    echo [run_isaac_sim_with_livesync] Set ISAACSIM_PATH to your install root and retry.
    exit /b 1
)

REM This tree ships inside the USD Toolkit package, so the Unity project root is not a fixed number
REM of levels up (embedded Packages\... vs Library\PackageCache\...). Walk up until a folder holding
REM both Assets\ and ProjectSettings\ turns up.
set PROJECT_ROOT=
for %%I in ("%~dp0.") do set CANDIDATE=%%~fI
:find_root
if exist "%CANDIDATE%\Assets" if exist "%CANDIDATE%\ProjectSettings" (
    set PROJECT_ROOT=%CANDIDATE%
    goto found_root
)
for %%I in ("%CANDIDATE%\..") do set PARENT=%%~fI
if /i "%PARENT%"=="%CANDIDATE%" goto found_root
set CANDIDATE=%PARENT%
goto find_root
:found_root

if "%PROJECT_ROOT%"=="" (
    echo [run_isaac_sim_with_livesync] Could not locate the Unity project root above "%~dp0".
    echo [run_isaac_sim_with_livesync] Set UNITY_USD_BASE_STAGE yourself and rerun.
) else (
    set UNITY_USD_BASE_STAGE=%PROJECT_ROOT%\UsdSync\base_stage.usda
)

echo [run_isaac_sim_with_livesync] ext folder : %~dp0exts
echo [run_isaac_sim_with_livesync] base stage : %UNITY_USD_BASE_STAGE%

call "%ISAACSIM_PATH%\isaac-sim.bat" --ext-folder "%~dp0exts" --enable unity.usd.livesync %*
exit /b %ERRORLEVEL%
