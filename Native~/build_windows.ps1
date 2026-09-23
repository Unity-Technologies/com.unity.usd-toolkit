param(
    [Parameter(Mandatory = $true)]
    [string] $OpenUsdRoot,

    [string] $Configuration = "Release",

    [string] $Generator = "Visual Studio 17 2022",

    [string] $CMakePath = "cmake"
)

$ErrorActionPreference = "Stop"

$NativeDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$PackageDir = Split-Path -Parent $NativeDir
$BuildDir = Join-Path $NativeDir "build~\windows-x64"
$InstallDir = Join-Path $PackageDir "Runtime\Plugins\x86_64\Windows"

if (-not (Test-Path $OpenUsdRoot)) {
    throw "OpenUSD root does not exist: $OpenUsdRoot"
}

$OpenUsdRoot = (Resolve-Path $OpenUsdRoot).Path
$CMakeCommand = Get-Command $CMakePath -ErrorAction SilentlyContinue
if ($null -eq $CMakeCommand) {
    throw "CMake was not found. Run this script from an x64 Native Tools prompt with CMake on PATH, or pass -CMakePath."
}

$UsdStageHeader = Join-Path $OpenUsdRoot "include\pxr\usd\usd\stage.h"
if (-not (Test-Path $UsdStageHeader)) {
    throw "OpenUSD headers were not found under $OpenUsdRoot. Expected: $UsdStageHeader"
}

New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

& $CMakeCommand.Source `
    -S $NativeDir `
    -B $BuildDir `
    -G $Generator `
    -A x64 `
    -DOPENUSD_ROOT="$OpenUsdRoot" `
    -DCMAKE_INSTALL_PREFIX="$InstallDir"

& $CMakeCommand.Source --build $BuildDir --config $Configuration --target install --parallel

$OpenUsdBin = Join-Path $OpenUsdRoot "bin"
$OpenUsdLib = Join-Path $OpenUsdRoot "lib"
$OpenUsdLibUsd = Join-Path $OpenUsdLib "usd"
$OpenUsdPlugin = Join-Path $OpenUsdRoot "plugin"
$OpenUsdShare = Join-Path $OpenUsdRoot "share"
$OpenUsdResources = Join-Path $OpenUsdRoot "resources"

function Copy-DllsFromDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceDir
    )

    if (-not (Test-Path $SourceDir)) {
        return
    }

    Get-ChildItem $SourceDir -Filter "*.dll" | Where-Object {
        $_.Name -like "usd*.dll" -or
        $_.Name -ieq "tbb.dll" -or
        $_.Name -like "zlib*.dll" -or
        $_.Name -like "boost*.dll"
    } | Where-Object {
        $_.Name -notlike "*_debug.dll" -and
        $_.Name -notlike "*debug*.dll"
    } | ForEach-Object {
        Copy-Item $_.FullName -Destination $InstallDir -Force
    }
}

function Copy-RuntimeDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceDir,

        [Parameter(Mandatory = $true)]
        [string] $DestinationName
    )

    if (-not (Test-Path $SourceDir)) {
        return
    }

    $Destination = Join-Path $InstallDir $DestinationName
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem $SourceDir -Force | ForEach-Object {
        Copy-Item $_.FullName -Destination $Destination -Recurse -Force
    }
}

Copy-DllsFromDirectory $OpenUsdBin
Copy-DllsFromDirectory $OpenUsdLib
Copy-RuntimeDirectory $OpenUsdLibUsd "lib\usd"
Copy-RuntimeDirectory $OpenUsdPlugin "plugin"
Copy-RuntimeDirectory $OpenUsdShare "share"
Copy-RuntimeDirectory $OpenUsdResources "resources"

$UsdCodegenTemplates = Join-Path $InstallDir "lib\usd\usd\resources\codegenTemplates"
if (Test-Path $UsdCodegenTemplates) {
    Remove-Item -LiteralPath $UsdCodegenTemplates -Recurse -Force
}
$UsdCodegenTemplatesMeta = "$UsdCodegenTemplates.meta"
if (Test-Path $UsdCodegenTemplatesMeta) {
    Remove-Item -LiteralPath $UsdCodegenTemplatesMeta -Force
}

$ToolkitDll = Join-Path $InstallDir "UnityUSDToolkitNative.dll"
if (-not (Test-Path $ToolkitDll)) {
    throw "UnityUSDToolkitNative.dll was not installed to $InstallDir"
}

Get-ChildItem $InstallDir -Recurse -Include "*.lib", "*.exp", "*.pdb" -File -ErrorAction SilentlyContinue | Remove-Item -Force

# --- Rename the OpenUSD monolithic library to something unique (avoids a base-name clash) ---
# OpenUSD's monolithic DLL is called 'usd_ms.dll', the same base name as the OpenUSD bundled by
# other packages (com.unity.pixyz.sdk-plus, for one). With both in a process, whichever loads
# first wins the binding, UnityUSDToolkitNative cannot find the exports it needs, and the result
# is DllNotFound (PROC_NOT_FOUND).
# So the payload's usd_ms.dll is renamed to a unique usd_rt.dll and the wrapper's import string
# is patched to match. (A PE import name cannot grow, hence 'usd_rt.dll' -- the same length as
# 'usd_ms.dll'.)
function Replace-AsciiInFile {
    param([string] $Path, [string] $From, [string] $To)
    if ($From.Length -ne $To.Length) { throw "rename length mismatch: '$From'($($From.Length)) vs '$To'($($To.Length))" }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $f = [Text.Encoding]::ASCII.GetBytes($From)
    $t = [Text.Encoding]::ASCII.GetBytes($To)
    $count = 0
    for ($i = 0; $i -le $bytes.Length - $f.Length; $i++) {
        $match = $true
        for ($j = 0; $j -lt $f.Length; $j++) { if ($bytes[$i + $j] -ne $f[$j]) { $match = $false; break } }
        if ($match) { for ($j = 0; $j -lt $t.Length; $j++) { $bytes[$i + $j] = $t[$j] }; $count++; $i += $f.Length - 1 }
    }
    [IO.File]::WriteAllBytes($Path, $bytes)
    return $count
}

$UsdMonolithic = Join-Path $InstallDir "usd_ms.dll"
if (Test-Path $UsdMonolithic) {
    $RenamedName = "usd_rt.dll"   # must match the length of 'usd_ms.dll' for the in-place patch
    [void](Replace-AsciiInFile -Path $ToolkitDll -From "usd_ms.dll" -To $RenamedName)
    [void](Replace-AsciiInFile -Path $UsdMonolithic -From "usd_ms.dll" -To $RenamedName)
    Move-Item -LiteralPath $UsdMonolithic -Destination (Join-Path $InstallDir $RenamedName) -Force
    Write-Host "Renamed OpenUSD monolithic usd_ms.dll -> $RenamedName and patched UnityUSDToolkitNative import."
}

$OpenUsdDlls = @(Get-ChildItem $InstallDir -Filter "*.dll" -ErrorAction SilentlyContinue)
if ($OpenUsdDlls.Count -le 1) {
    Write-Warning "Only UnityUSDToolkitNative.dll was found in the plugin folder. Copy OpenUSD runtime DLLs manually if your OpenUSD install keeps them outside bin/lib."
}

Write-Host ""
Write-Host "Installed UnityUSDToolkitNative and OpenUSD runtime files to:"
Write-Host $InstallDir
