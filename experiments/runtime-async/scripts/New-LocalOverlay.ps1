<#
.SYNOPSIS
Stages two flattened shared-framework overlays (runtime-async ON and OFF) from the
locally built dotnet/runtime and dotnet/aspnetcore worktrees.

.DESCRIPTION
Round 1 used official build-cache-service bits. Those turned out to be compiled with
runtime-async ON, so they cannot serve as an "off" baseline. This script builds both
arms from local builds of the *same* commits, configured identically apart from
/p:UseRuntimeAsync, so the only variable between arms is the feature itself.

Layout mirrors New-Overlay in Get-BcsBits.ps1: runtime lib/net11.0, then runtime
native, then aspnetcore lib/net11.0 last so the ASP.NET layer wins name collisions.

Emits local-bits.json, which Invoke-RuntimeAsyncBenchmarks.ps1 reads to locate the
overlays.
#>
[CmdletBinding()]
param(
    [string] $WslDistro     = 'Ubuntu-24.04',
    [string] $WslUser       = 'anscoggi',
    [string] $RuntimeOnWt   = 'runtime-on',
    [string] $RuntimeOffWt  = 'runtime-noasync',
    [string] $AspNetOnWt    = 'aspnetcore',
    [string] $AspNetOffWt   = 'aspnetcore-off',
    [string] $RuntimeSha    = 'fdccdc6954791fcde7ffa2834d75930c0efa5456',
    [string] $AspNetSha     = '747d2cdb584079a0c7309115979f13c331fb7df7',
    [ValidateSet('x64', 'arm64')]
    [string] $Arch          = 'x64',
    # Namespaces the staged output so successive rounds/architectures do not clobber
    # one another, e.g. -Tag r3-x64 produces overlay\r3-x64-on and overlay\r3-x64-off.
    [string] $Tag           = '',
    # Directory of pre-archived aspnetcore packs named aspnet-<arch>-<flavor>.nupkg.
    # Preferred over reading the worktree, because both architectures share a worktree
    # and each build wipes artifacts/, so only the most recent arch survives there.
    [string] $AspNetPackDir = '',
    [string] $BitsFile      = 'local-bits.json',
    [string] $Root          = $PSScriptRoot,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$wslHome  = "\\wsl.localhost\$WslDistro\home\$WslUser\ra"
$stageDir = Join-Path $Root 'local'
$overlayDir = Join-Path $Root 'overlay'

# Debug symbols and static libs roughly triple the payload without affecting what
# the app actually executes.
$excludeExt = @('.dbg', '.pdb', '.a', '.lib', '.h')

function New-Dir([string] $p) {
    if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
    return $p
}

function Copy-OverlayFiles([string] $Source, [string] $Destination, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Expected overlay source not found: $Source" }
    $files = @(Get-ChildItem -LiteralPath $Source -File |
        Where-Object { $excludeExt -notcontains $_.Extension })
    if ($files.Count -eq 0) { throw "No files to copy from $Source" }
    foreach ($f in $files) { Copy-Item -LiteralPath $f.FullName -Destination $Destination -Force }
    $mb = ($files | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("    {0,-16} {1,4} files  {2,7:N1} MB" -f $Label, $files.Count, $mb)
}

<#
The aspnetcore build produces a nupkg rather than a loose layout, so unpack it into
the staging dir. Returns the runtimes/linux-<arch>/lib/net11.0 folder inside.
#>
function Expand-AspNetPack([string] $Worktree, [string] $Slug, [string] $Flavor) {
    if ($AspNetPackDir) {
        $src = Join-Path $AspNetPackDir "aspnet-$Arch-$Flavor.nupkg"
        if (-not (Test-Path -LiteralPath $src)) { throw "archived pack not found: $src" }
        $nupkg = @(Get-Item -LiteralPath $src)
    }
    else {
        $pkgRoot = Join-Path $wslHome "$Worktree\artifacts\packages"
        if (-not (Test-Path -LiteralPath $pkgRoot)) { throw "aspnetcore packages dir not found: $pkgRoot" }
        $nupkg = @(Get-ChildItem -LiteralPath $pkgRoot -Recurse -Filter "Microsoft.AspNetCore.App.Runtime.linux-$Arch.*.nupkg" -File |
            Where-Object { $_.Name -notmatch '\.symbols\.nupkg$' } |
            Sort-Object LastWriteTime -Descending)
        if ($nupkg.Count -eq 0) { throw "No Microsoft.AspNetCore.App.Runtime.linux-$Arch nupkg under $pkgRoot" }
    }

    $dest = Join-Path $stageDir "aspnet-$Slug"
    if ((Test-Path -LiteralPath $dest) -and $Force) { Remove-Item -LiteralPath $dest -Recurse -Force }
    if (-not (Test-Path -LiteralPath $dest)) {
        New-Dir $dest | Out-Null
        Write-Host "    unpacking $($nupkg[0].Name)"
        # Copy locally first; Expand-Archive over the WSL UNC share is slow and flaky.
        $tmp = Join-Path $stageDir "$($nupkg[0].BaseName).zip"
        Copy-Item -LiteralPath $nupkg[0].FullName -Destination $tmp -Force
        Expand-Archive -LiteralPath $tmp -DestinationPath $dest -Force
        Remove-Item -LiteralPath $tmp -Force
    }
    else {
        Write-Host "    aspnet-$Slug (cached)"
    }

    $lib = Join-Path $dest "runtimes\linux-$Arch\lib\net11.0"
    if (-not (Test-Path -LiteralPath $lib)) { throw "Unexpected nupkg layout, missing: $lib" }
    return $lib
}

function New-LocalOverlay([string] $RuntimeWt, [string] $AspNetWt, [string] $Flavor) {
    $slug = if ($Tag) { "$Tag-$Flavor" } else { $Flavor }
    Write-Host "  overlay [$slug]" -ForegroundColor Cyan

    $dest = Join-Path $overlayDir $slug
    if ((Test-Path -LiteralPath $dest) -and $Force) { Remove-Item -LiteralPath $dest -Recurse -Force }
    $existing = @()
    if (Test-Path -LiteralPath $dest) { $existing = @(Get-ChildItem -LiteralPath $dest -File) }
    if ($existing.Count -gt 0) {
        Write-Host "    (cached) $dest  [$($existing.Count) files]"
        return $dest
    }

    $aspLib = Expand-AspNetPack -Worktree $AspNetWt -Slug $slug -Flavor $Flavor

    New-Dir $dest | Out-Null
    $rtPack = Join-Path $wslHome "$RuntimeWt\artifacts\bin\microsoft.netcore.app.runtime.linux-$Arch\Release\runtimes\linux-$Arch"
    Copy-OverlayFiles -Source (Join-Path $rtPack 'lib\net11.0') -Destination $dest -Label 'runtime lib'
    Copy-OverlayFiles -Source (Join-Path $rtPack 'native')      -Destination $dest -Label 'runtime native'
    Copy-OverlayFiles -Source $aspLib                            -Destination $dest -Label 'aspnet lib'

    $all = @(Get-ChildItem -LiteralPath $dest -File)
    Write-Host ("    {0,-16} {1,4} files  {2,7:N1} MB" -f 'TOTAL', $all.Count, (($all | Measure-Object Length -Sum).Sum / 1MB))
    return $dest
}

# ---------------------------------------------------------------------------

New-Dir $stageDir   | Out-Null
New-Dir $overlayDir | Out-Null

Write-Host "Staging local runtime-async overlays" -ForegroundColor Cyan
Write-Host "  runtime    $RuntimeSha"
Write-Host "  aspnetcore $AspNetSha"
Write-Host "  arch       $Arch"
if ($Tag) { Write-Host "  tag        $Tag" }

$onPath  = New-LocalOverlay -RuntimeWt $RuntimeOnWt  -AspNetWt $AspNetOnWt  -Flavor 'on'
$offPath = New-LocalOverlay -RuntimeWt $RuntimeOffWt -AspNetWt $AspNetOffWt -Flavor 'off'

$bits = [ordered]@{
    OverlayOn   = $onPath
    OverlayOff  = $offPath
    RuntimeSha  = $RuntimeSha
    AspNetSha   = $AspNetSha
    Arch        = $Arch
    Tag         = $Tag
    Source      = 'local-build'
    StagedUtc   = (Get-Date).ToUniversalTime().ToString('o')
}
$json = Join-Path $Root $BitsFile
$bits | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $json -Encoding UTF8
Write-Host ""
Write-Host "Wrote $json" -ForegroundColor Green
