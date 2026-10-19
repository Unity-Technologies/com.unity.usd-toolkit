@echo off
REM ---------------------------------------------------------------------------------------------
REM AGV fleet demo - Isaac Sim side. Runs launch_agv_fleet.py under Isaac Sim's own interpreter,
REM in its own console window, and relaunches it if it crashes during start-up (run_agv_fleet.ps1).
REM
REM   run_agv_fleet.bat                 3 AGVs, GUI, factory environment
REM   run_agv_fleet.bat --agvs 1        single-AGV demo
REM   run_agv_fleet.bat --env warehouse other environment preset (see config.py)
REM   run_agv_fleet.bat --mode sync     step-1 test: AGV_01 drives a circle, no planner
REM   run_agv_fleet.bat --headless
REM
REM Override the install location with:  set ISAACSIM_PATH=D:\path\to\isaacsim
REM ---------------------------------------------------------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run_agv_fleet.ps1" %*
exit /b %ERRORLEVEL%
