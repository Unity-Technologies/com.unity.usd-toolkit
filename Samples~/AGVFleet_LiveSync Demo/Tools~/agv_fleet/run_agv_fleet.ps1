# AGV fleet demo - Isaac Sim launcher with crash retry (called by run_agv_fleet.bat).
#
# Isaac Sim 6.1 RC occasionally crashes during start-up inside omni.graph.core (engine side; seen
# mostly when it is started right after another Isaac process ended). A crash takes down the whole
# console process chain, so the retry cannot live in the same console: this supervisor starts Isaac
# in its own window, waits, and relaunches it after a crash (non-zero exit), up to 3 attempts.
# A normal close (or --seconds) exits with 0 and is not retried.

$ErrorActionPreference = "Stop"
# Isaac Sim location: ISAACSIM_PATH, else isaacsim_path.txt next to this script (one line: the folder
# that holds python.bat), else the usual install folders.
$pathFile = Join-Path $PSScriptRoot "isaacsim_path.txt"
$candidates = @()
if ($env:ISAACSIM_PATH) { $candidates += $env:ISAACSIM_PATH }
if (Test-Path $pathFile) { $candidates += (Get-Content $pathFile -TotalCount 1).Trim().Trim('"') }
$candidates += @("C:\isaacsim", "C:\isaac-sim",
                 (Join-Path $env:USERPROFILE "isaacsim"), (Join-Path $env:USERPROFILE "isaac-sim"))
$isaac = $candidates | Where-Object { $_ -and (Test-Path (Join-Path $_ "python.bat")) } | Select-Object -First 1
if (-not $isaac) {
    Write-Host "[run_agv_fleet] Isaac Sim 6.1 not found. Looked in:"
    $candidates | Where-Object { $_ } | ForEach-Object { Write-Host "    $_" }
    Write-Host "[run_agv_fleet] Write your Isaac Sim folder (the one with python.bat) into $pathFile, or set ISAACSIM_PATH."
    exit 1
}
Write-Host "[run_agv_fleet] Isaac Sim: $isaac"
$python = Join-Path $isaac "python.bat"
$script = Join-Path $PSScriptRoot "launch_agv_fleet.py"
$quoted = ($args | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
$cmdLine = "/c `"`"$python`" `"$script`" --no-ros-env $quoted`""

for ($attempt = 1; $attempt -le 3; $attempt++) {
    $t0 = Get-Date
    Write-Host "[run_agv_fleet] starting Isaac Sim (attempt $attempt of 3) ..."
    $p = Start-Process -FilePath "cmd.exe" -ArgumentList $cmdLine -PassThru -Wait -WindowStyle Normal
    $secs = [int]((Get-Date) - $t0).TotalSeconds
    # python.bat tests "if errorlevel 1", which misses negative codes such as an access violation
    # (0xC0000005), so a crash can still exit 0: also look for the crash marker in this run's Kit log.
    $crashed = $p.ExitCode -ne 0
    if (-not $crashed) {
        $log = Get-ChildItem (Join-Path $isaac "kit\logs\Kit") -Recurse -Filter "kit_*.log" -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $t0 } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $crashed = $log -and (Select-String -Path $log.FullName -Pattern "A crash has occurred" -SimpleMatch -Quiet)
    }
    if (-not $crashed) { exit 0 }
    Write-Host "[run_agv_fleet] Isaac Sim crashed (exit code $($p.ExitCode)) after $secs s."
    if ($attempt -lt 3) { Start-Sleep -Seconds 5 }   # let the crashed process release GPU / caches
}
Write-Host "[run_agv_fleet] giving up after 3 attempts."
exit 1
