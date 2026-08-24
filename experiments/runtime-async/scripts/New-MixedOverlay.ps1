<#
.SYNOPSIS
Composes a fourth-arm overlay: runtime built runtime-async ON, ASP.NET Core built
runtime-async OFF.

.DESCRIPTION
The three existing arms cannot separate the runtime layer's cost from the ASP.NET
layer's, because `app-off` turns both on at once:

    all-off   runtime OFF   aspnet OFF   app OFF
    app-off   runtime ON    aspnet ON    app OFF     <- both layers move together
    all-on    runtime ON    aspnet ON    app ON

This adds:

    rt-on     runtime ON    aspnet OFF   app OFF

so that (rt-on - all-off) isolates the runtime layer and (app-off - rt-on) isolates
the ASP.NET layer.

No rebuild is required. The staged overlays are flat directories built as
runtime lib -> runtime native -> aspnet lib (aspnet copied last, §New-LocalOverlay),
and the two layers turn out to be disjoint by filename (317 runtime + 134 aspnet =
451). So the mixed overlay is exactly the ON overlay with its 134 ASP.NET files
replaced by the OFF build's copies of the same 134 names. Both flavors come from the
same aspnetcore commit, so the name sets are identical and the substitution is total.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string] $Arch = 'x64',
    [string] $Tag  = '',
    [string] $Root = $PSScriptRoot,
    # Newline-separated list of the ASP.NET layer's filenames. Only needed when the
    # local/aspnet-<tag>-off staging folder is absent (e.g. inside a repro bundle);
    # the same files are then taken from the OFF overlay, which is byte-identical.
    [string] $AspNetFileList = '',
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$tag       = if ($Tag) { $Tag } else { "r3-$Arch" }
$onDir     = Join-Path $Root "overlay\$tag-on"
$offDir    = Join-Path $Root "overlay\$tag-off"
$aspOffLib = Join-Path $Root "local\aspnet-$tag-off\runtimes\linux-$Arch\lib\net11.0"
$dest      = Join-Path $Root "overlay\$tag-rton"

foreach ($p in @($onDir, $offDir)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Required input missing: $p" }
}

# Prefer the original staging folder; fall back to the OFF overlay, which already
# contains those same ASP.NET files (New-LocalOverlay copies the aspnet layer last).
if (Test-Path -LiteralPath $aspOffLib) {
    $aspSource = $aspOffLib
    $aspNames  = @(Get-ChildItem -LiteralPath $aspOffLib -File |
        Where-Object { @('.dbg', '.pdb', '.a', '.lib', '.h') -notcontains $_.Extension } |
        Select-Object -ExpandProperty Name)
}
else {
    if (-not $AspNetFileList) {
        $AspNetFileList = Join-Path $Root 'bits\aspnet-layer-files.txt'
    }
    if (-not (Test-Path -LiteralPath $AspNetFileList)) {
        throw "Neither the staging folder ($aspOffLib) nor an ASP.NET file list ($AspNetFileList) was found."
    }
    $aspSource = $offDir
    $aspNames  = @(Get-Content -LiteralPath $AspNetFileList | Where-Object { $_.Trim() })
    Write-Host "  (using the OFF overlay as the ASP.NET source; $($aspNames.Count) names from $AspNetFileList)"
}

if ((Test-Path -LiteralPath $dest) -and $Force) { Remove-Item -LiteralPath $dest -Recurse -Force }
if (Test-Path -LiteralPath $dest) {
    $n = @(Get-ChildItem -LiteralPath $dest -File).Count
    Write-Host "  (cached) $dest [$n files] - use -Force to rebuild" -ForegroundColor Yellow
    return
}

Write-Host "Composing mixed overlay [$tag-rton]" -ForegroundColor Cyan
Write-Host "  runtime layer  <- $tag-on"
Write-Host "  aspnet  layer  <- $tag-off"

New-Item -ItemType Directory -Path $dest -Force | Out-Null

# 1. Start from the fully-ON overlay.
Copy-Item -Path (Join-Path $onDir '*') -Destination $dest -Force
$baseCount = @(Get-ChildItem -LiteralPath $dest -File).Count
Write-Host ("    {0,-16} {1,4} files" -f 'base (ON)', $baseCount)

# 2. Overwrite every ASP.NET file with the OFF build's copy.
$replaced = 0
foreach ($name in $aspNames) {
    $src    = Join-Path $aspSource $name
    $target = Join-Path $dest $name
    if (-not (Test-Path -LiteralPath $src))    { throw "ASP.NET source file missing: $src" }
    if (-not (Test-Path -LiteralPath $target)) { throw "ASP.NET file has no counterpart in the ON overlay: $name" }
    Copy-Item -LiteralPath $src -Destination $target -Force
    $replaced++
}
Write-Host ("    {0,-16} {1,4} files" -f 'aspnet (OFF)', $replaced)

$all = @(Get-ChildItem -LiteralPath $dest -File)
if ($all.Count -ne $baseCount) { throw "File count changed: $baseCount -> $($all.Count)" }

# Every file must be byte-identical to whichever source layer it came from.
$bad = 0
foreach ($f in $all) {
    $src = if ($aspNames -contains $f.Name) { Join-Path $aspSource $f.Name } else { Join-Path $onDir $f.Name }
    if ((Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $src        -Algorithm SHA256).Hash) {
        Write-Warning "hash mismatch: $($f.Name)"; $bad++
    }
}
if ($bad) { throw "$bad file(s) did not match their source layer" }

Write-Host ("    {0,-16} {1,4} files  {2,7:N1} MB  (all {1} hash-verified)" -f `
    'TOTAL', $all.Count, (($all | Measure-Object Length -Sum).Sum / 1MB))
Write-Host "Wrote $dest" -ForegroundColor Green
