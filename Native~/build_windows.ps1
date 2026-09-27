param(
    [Parameter(Mandatory = $true)]
    [string] $OpenUsdRoot,

    [string] $Configuration = "Release",

    [string] $Generator = "Visual Studio 17 2022",

    [string] $CMakePath = "cmake",

    # Accept the current OpenUSD/TBB tree and rewrite Native~/dependency-digests/windows.sha256
    # instead of verifying against it. Commit the result so the change is reviewable.
    [switch] $RecordDependencyDigests,

    # Authenticode signing, applied after the in-place import patch below -- patching a signed
    # DLL would invalidate the signature, so signing has to come last.
    [string] $SignToolPath = "signtool",
    [string] $SigningCertificateThumbprint = "",
    [string] $TimestampUrl = "http://timestamp.digicert.com",

    # Produce unsigned DLLs on purpose. Acceptable for local work, not for a release.
    [switch] $SkipSigning,

    # Build against an OpenUSD install that was never verified against
    # Native~/dependency-sources/windows.tsv. For local experiments only: the resulting payload
    # has no chain back to a published revision and must not be committed or shipped.
    [switch] $SkipSourceProvenance
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
    # Clear the destination first, as the macOS and Linux scripts do. Copying over the top left
    # plugin directories from a previous OpenUSD version behind: usdLuxValidators exists only in
    # 26.08 and survived the 26.05 rebuild this way, so a 26.05 runtime was registering a plugin
    # descriptor from a version it was not built from -- and the integrity manifest then recorded
    # that stale file as expected content.
    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem $SourceDir -Force | ForEach-Object {
        Copy-Item $_.FullName -Destination $Destination -Recurse -Force
    }
}

# --- Source provenance gate (SECURITY-282834, trust chain) ---------------------------------
# The digest gate below establishes that the tree being copied is the tree that was reviewed. It
# cannot establish where that tree came from -- by this point OpenUSD has already been fetched and
# compiled. That earlier link is what the stamp carries: verify_upstream_sources.py writes it into
# the install root only after checking the clone against the commit pinned in
# Native~/dependency-sources/windows.tsv, so requiring it here is what ties the payload to a
# published revision rather than to whatever happened to be on this machine.
$ProvenanceScript = Join-Path $NativeDir "verify_upstream_sources.py"
if ($SkipSourceProvenance) {
    Write-Warning "-SkipSourceProvenance. This payload has no chain back to a published OpenUSD revision."
    Write-Warning "Do not commit or ship what this build produces."
} else {
    & python "$ProvenanceScript" --platform windows --check-stamp "$OpenUsdRoot"
    if ($LASTEXITCODE -ne 0) {
        throw "Refusing to build against an OpenUSD install of unverified origin."
    }
}

# --- Dependency digest gate (SECURITY-282834, CWE-347) -------------------------------------
# Everything below is copied verbatim off this machine into the shipped package, so a tampered
# local OpenUSD/TBB tree would ride in unnoticed. Check what is about to be copied against the
# checked-in record first.
$DependencyFiles = @()
foreach ($dir in @($OpenUsdBin, $OpenUsdLib)) {
    if (Test-Path $dir) {
        $DependencyFiles += Get-ChildItem $dir -Filter "*.dll" -File |
            Where-Object { $_.Name -notlike "*debug*" } | ForEach-Object { $_.FullName }
    }
}
foreach ($dir in @($OpenUsdLibUsd, $OpenUsdPlugin, $OpenUsdShare, $OpenUsdResources)) {
    if (Test-Path $dir) {
        $DependencyFiles += Get-ChildItem $dir -Recurse -File |
            Where-Object { $_.Extension -ne ".meta" } | ForEach-Object { $_.FullName }
    }
}

$DependencyMode = if ($RecordDependencyDigests) { "--record" } else { "--verify" }
$VerifyScript = Join-Path $NativeDir "verify_dependency_digests.py"
& python "$VerifyScript" --platform windows --root "$OpenUsdRoot" $DependencyMode @DependencyFiles
if ($LASTEXITCODE -ne 0) {
    throw "Refusing to copy an unverified dependency tree into the package."
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

# --- Rename Intel's TBB for the same reason (SECURITY-282834) -------------------------------
# The Unity Editor ships its own tbb.dll in Frameworks, and Windows resolves an import by base
# name against what is already loaded: whichever tbb.dll got there first wins, and the payload
# runs against a build it was not compiled for. Both happen to be TBB 2020.3 today, so it works
# by coincidence rather than by contract, and a coincidence is not something to ship on.
#
# Only usd_rt.dll imports tbb.dll, and its import name string has no room to grow -- seven
# characters, a NUL, then the next entry. So patch_pe_import.py redirects the import descriptor
# instead of overwriting the string; see its docstring. The DLL itself is untouched, which is what
# keeps Intel's Authenticode signature on it valid: Authenticode covers a PE's contents, not its
# filename.
$TbbDll = Join-Path $InstallDir "tbb.dll"
if (Test-Path $TbbDll) {
    $TbbRenamed = "tbb_usdrt.dll"
    $UsdRuntimeDll = Join-Path $InstallDir "usd_rt.dll"
    $PatchImportScript = Join-Path $NativeDir "patch_pe_import.py"
    & python "$PatchImportScript" "$UsdRuntimeDll" --from "tbb.dll" --to $TbbRenamed
    if ($LASTEXITCODE -ne 0) {
        throw "Could not repoint usd_rt.dll's TBB import; refusing to ship a payload that would bind to the Editor's tbb.dll."
    }
    Move-Item -LiteralPath $TbbDll -Destination (Join-Path $InstallDir $TbbRenamed) -Force
    Write-Host "Renamed Intel tbb.dll -> $TbbRenamed and repointed usd_rt.dll's import."
}

# --- Authenticode signing (SECURITY-282834, CWE-347) ---------------------------------------
# Last step on purpose: the in-place import patch above rewrites bytes inside the DLL, which
# would invalidate a signature applied before it. Failing here rather than shipping unsigned is
# the point -- a silent fallback is how unsigned artifacts end up released.
if (-not $SkipSigning) {
    if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
        throw @"
No signing certificate given, so the DLLs would ship unsigned and the OS could not tell whether
they had been altered.
  -SigningCertificateThumbprint <thumbprint>  sign with that certificate (use this for a release)
  -SkipSigning                                produce unsigned DLLs on purpose (local work only)
"@
    }

    $ToSign = @(Get-ChildItem $InstallDir -Filter "*.dll" -File | ForEach-Object { $_.FullName })
    if ($ToSign.Count -gt 0) {
        & $SignToolPath sign /fd SHA256 /sha1 $SigningCertificateThumbprint `
            /tr $TimestampUrl /td SHA256 @ToSign
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode signing failed; refusing to leave unsigned DLLs in the payload."
        }

        & $SignToolPath verify /pa @ToSign
        if ($LASTEXITCODE -ne 0) {
            throw "Signature verification failed after signing."
        }

        Write-Host "Signed and verified $($ToSign.Count) DLL(s)."
    }
}
else {
    Write-Warning "Signing skipped. The resulting DLLs carry no publisher identity and must not be released."
}

$OpenUsdDlls = @(Get-ChildItem $InstallDir -Filter "*.dll" -ErrorAction SilentlyContinue)
if ($OpenUsdDlls.Count -le 1) {
    Write-Warning "Only UnityUSDToolkitNative.dll was found in the plugin folder. Copy OpenUSD runtime DLLs manually if your OpenUSD install keeps them outside bin/lib."
}

Write-Host ""
Write-Host "Installed UnityUSDToolkitNative and OpenUSD runtime files to:"
Write-Host $InstallDir
