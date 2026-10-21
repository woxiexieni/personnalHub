param(
    [Parameter(Mandatory = $true)][string]$ApkPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][int]$Code,
    [Parameter(Mandatory = $true)][string]$UpdatesDir,
    [string]$Abi = 'arm64',
    [int]$Keep = 3
)

# ---------------------------------------------------------------------------
# Delivers a freshly built APK to the server's download folder so the phone can
# fetch it over Wi-Fi instead of over USB.
#
# Called by client-android-release.bat right after the APK is copied to
# out\android. Writes two things into $UpdatesDir:
#
#   personalhub-<version>-<abi>.apk   the package itself, with a stable name
#   latest.json                       version / code / size / sha256 / builtAt
#
# Why a separate .ps1 and not inline -Command: this needs string manipulation,
# hashing and a pipeline; cmd escapes pipes as ^| which then reaches PowerShell
# literally ("A positional parameter cannot be found"). Same lesson as
# find-fresh-apk.ps1.
#
# Why sha256 is computed here rather than by the server: the client-side updater
# (step 2) downloads 19 MB over Wi-Fi and MUST be able to tell "complete file"
# from "half a file before the Wi-Fi dropped". The server would have to hash on
# every request to offer that, and the value would then describe the file as it
# is now rather than the file that was built. Computing it once at build time is
# both cheaper and more honest.
#
# Why copy to .tmp and then move: the server may be serving a download of the
# previous build at that exact moment. Writing straight into the final name lets
# a client read a half-overwritten file, and the size check would not catch it
# because the size is only right at the end. Rename is atomic on the same volume.
#
# ASCII only in this file: PowerShell 5.1 reads a BOM-less .ps1 using the OEM
# code page, so UTF-8 Chinese turns into garbage here. All user-facing Chinese
# is printed by the .NET server, not by these scripts.
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) {
    Write-Output ''
    Write-Output "[!] $msg"
    exit 1
}

if (-not (Test-Path -LiteralPath $ApkPath)) {
    Fail "APK not found: $ApkPath"
}

$src = Get-Item -LiteralPath $ApkPath
$size = $src.Length

# Sanity gate. A Release APK is ~19 MB (26 MB with AOT); Debug is ~120 MB and
# must never be shipped; a 10 MB package means EmbedAssembliesIntoApk silently
# turned off (the "empty shell APK" failure) and would crash on launch.
if ($size -lt 15MB) {
    $msg = ("APK is only {0:N1} MB. A Release build is ~19 MB; this looks like an " -f ($size / 1MB))
    $msg += "empty-shell package (assemblies not embedded). Check EmbedAssembliesIntoApk. Not delivered."
    Fail $msg
}
if ($size -gt 40MB) {
    Write-Output ("[!] Warning: {0:N1} MB is larger than a Release build (~19 MB)." -f ($size / 1MB))
    Write-Output "    This may be a Debug build, which is not meant to be installed by hand."
}

if (-not (Test-Path -LiteralPath $UpdatesDir)) {
    New-Item -ItemType Directory -Path $UpdatesDir -Force | Out-Null
}

$fileName = "personalhub-$Version-$Abi.apk"
$target = Join-Path $UpdatesDir $fileName
$sha = (Get-FileHash -LiteralPath $src.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

$changed = $true
if (Test-Path -LiteralPath $target) {
    $oldSize = (Get-Item -LiteralPath $target).Length
    $oldSha = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($oldSize -eq $size -and $oldSha -eq $sha) {
        $changed = $false
        Write-Output "[--] $fileName is already there with identical content - not copying again."
    }
    else {
        Write-Output "[*] Replacing $fileName (content changed)."
    }
}

if ($changed) {
    $tmp = "$target.tmp"
    try {
        Copy-Item -LiteralPath $src.FullName -Destination $tmp -Force
        # Verify the copy actually landed before it becomes visible under the real name.
        if ((Get-Item -LiteralPath $tmp).Length -ne $size) {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            Fail "Copy produced a different size. Nothing delivered."
        }
        Move-Item -LiteralPath $tmp -Destination $target -Force
    }
    catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        Fail ("Could not replace $fileName - the server may be serving it right now. " +
              "Close that download and run this again.")
    }
}

# ---- latest.json (written last, so it never points at a file that is not there)
$manifest = [ordered]@{
    version = $Version
    code    = $Code
    file    = $fileName
    size    = $size
    sha256  = $sha
    builtAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
}

$json = $manifest | ConvertTo-Json
$jsonPath = Join-Path $UpdatesDir 'latest.json'
$jsonTmp = "$jsonPath.tmp"

# Write without a BOM: the .NET reader tolerates one, but a BOM here makes the
# file look broken in every other tool (git diff, notepad, curl).
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($jsonTmp, $json, $utf8NoBom)
Move-Item -LiteralPath $jsonTmp -Destination $jsonPath -Force

# ---- retention: keep the newest $Keep packages, drop the rest
# Sorted by [version], not by file name: "1.10.0" sorts before "1.9.0" as text.
$items = @()
foreach ($f in @(Get-ChildItem -LiteralPath $UpdatesDir -Filter 'personalhub-*.apk')) {
    if ($f.Name -match '^personalhub-(\d+\.\d+\.\d+)-') {
        $items += [pscustomobject]@{ File = $f; Ver = [version]$Matches[1] }
    }
}

$removed = 0
$sorted = @($items | Sort-Object -Property Ver -Descending)
if ($sorted.Count -gt $Keep) {
    foreach ($old in @($sorted | Select-Object -Skip $Keep)) {
        try {
            Remove-Item -LiteralPath $old.File.FullName -Force
            $removed++
        }
        catch {
            Write-Output "[!] Could not remove old package $($old.File.Name)"
        }
    }
}

Write-Output ''
Write-Output "[OK] delivered $fileName"
Write-Output ("     size    : {0:N0} bytes ({1:N1} MB)" -f $size, ($size / 1MB))
Write-Output ("     sha256  : $sha")
Write-Output ("     version : $Version  (versionCode $Code)")
Write-Output ("     folder  : $UpdatesDir")
Write-Output ("     kept {0} package(s), removed {1}" -f [Math]::Min($sorted.Count, $Keep), $removed)
Write-Output ''
Write-Output "     Phone: open http://<this-pc-ip>:5001/app in a browser and tap download."
Write-Output "     (The server prints its own address when it starts.)"
