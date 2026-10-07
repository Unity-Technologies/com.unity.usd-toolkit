@echo off
REM ---------------------------------------------------------------------------------------------
REM Standalone Isaac Sim client for the Unity USD live transform sync.
REM
REM Must run under Isaac Sim's OWN interpreter (python.bat) — a system Python has no pxr/omni.
REM Override the install location with:  set ISAACSIM_PATH=D:\path\to\isaacsim
REM
REM   run_isaac_live_sync.bat
REM   run_isaac_live_sync.bat --headless --seconds 30
REM   run_isaac_live_sync.bat --mode open
REM   run_isaac_live_sync.bat --push-back /SyncRoot/Box_100x100x100_Prefab_18_
REM ---------------------------------------------------------------------------------------------
setlocal

REM No ISAACSIM_PATH: probe the conventional install locations. Each line is parsed fresh, so the
REM "still empty" guard sees the previous line's assignment (a for-loop here would not).
if "%ISAACSIM_PATH%"=="" if exist "C:\isaacsim\python.bat" set ISAACSIM_PATH=C:\isaacsim
if "%ISAACSIM_PATH%"=="" if exist "%USERPROFILE%\isaacsim\python.bat" set ISAACSIM_PATH=%USERPROFILE%\isaacsim

if "%ISAACSIM_PATH%"=="" (
    echo [run_isaac_live_sync] Isaac Sim not found in the conventional locations
    echo [run_isaac_live_sync]   C:\isaacsim
    echo [run_isaac_live_sync]   %USERPROFILE%\isaacsim
    echo [run_isaac_live_sync] Set ISAACSIM_PATH to your install root and retry:
    echo [run_isaac_live_sync]   set ISAACSIM_PATH=D:\path\to\isaacsim
    exit /b 1
)

if not exist "%ISAACSIM_PATH%\python.bat" (
    echo [run_isaac_live_sync] No python.bat under "%ISAACSIM_PATH%".
    echo [run_isaac_live_sync] Set ISAACSIM_PATH to your install root and retry.
    exit /b 1
)

call "%ISAACSIM_PATH%\python.bat" "%~dp0isaac_live_sync_standalone.py" %*
exit /b %ERRORLEVEL%
