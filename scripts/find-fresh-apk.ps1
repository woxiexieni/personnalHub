param(
    # The repo root, not a list of folders: passing two paths through a batch
    # FOR /F + PowerShell binding turned into "a positional parameter cannot be
    # found" twice in a row, so the two folders this cares about live inside here.
    [Parameter(Mandatory = $true)][string]$RepoRoot,
    [Parameter(Mandatory = $true)][long]$SinceTicks
)

# Prints the newest *-Signed.apk under the repo that was written after $SinceTicks.
# Prints nothing when there is none -- the caller treats empty as "fail".
#
# Why a separate .ps1 instead of an inline -Command:
# the inline version needed pipelines, and pipelines inside a batch FOR /F
# must be escaped as ^| which then reaches PowerShell as a literal argument
# ("A positional parameter cannot be found that accepts argument '^'").
# A file has no such problem, and it is readable.
#
# Why these two folders and not the whole repo: MSBuild has been seen writing the
# package to out\android instead of bin\Release\...\publish (see the note in
# client-android-release.bat). Searching both means "where it landed" stops
# mattering, without crawling .git on every run.

$roots = @(
    (Join-Path $RepoRoot 'PersonalHub.Client'),
    (Join-Path $RepoRoot 'out')
)

$files = @()
foreach ($r in $roots) {
    if (Test-Path -LiteralPath $r) {
        $files += @(Get-ChildItem -LiteralPath $r -Recurse -Filter '*-Signed.apk' -ErrorAction SilentlyContinue)
    }
}

$best = $null
$bestTicks = $SinceTicks

foreach ($f in $files) {
    $t = $f.LastWriteTimeUtc.Ticks
    if ($t -gt $bestTicks) {
        $best = $f.FullName
        $bestTicks = $t
    }
}

if ($null -ne $best) { Write-Output $best }
