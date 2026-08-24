<#
.SYNOPSIS
Packages every crank invocation, every input artifact, and every result from the
runtime-async experiment into one self-contained zip that someone else can unpack
and re-run.

.DESCRIPTION
Produces:

  README.md                 prerequisites + step-by-step repro
  commands/                 every crank command line, verbatim and portable
  scripts/                  the harness (runner, overlay composer, aggregators)
  bits/                     overlay provenance + SHA256 of every staged file
  results/                  the result JSON from every run, plus aggregated CSVs
  overlays/                 the actual shared-framework overlays (optional)
  FINDINGS.md               the full written analysis

Command lines are rewritten so the absolute paths of this machine become
$BundleRoot / $BenchmarksRoot placeholders, then Run-Repro.ps1 substitutes the
unpacker's own paths back in.

.PARAMETER Overlays
  none    omit the binaries entirely (smallest; repro requires rebuilding)
  source  include the ON and OFF overlays only; the mixed rt-on overlay is
          re-derived locally by New-MixedOverlay.ps1 (default)
  all     include ON, OFF and the mixed rt-on overlay
#>
[CmdletBinding()]
param(
    [string[]] $RunId = @('r3-gold-x64', 'r3-cloud-arm64', 'cb200-4c'),
    [ValidateSet('none', 'source', 'all')]
    [string]   $Overlays = 'source',
    [string]   $Root = $PSScriptRoot,
    [string]   $BenchmarksRoot = 'D:\git\benchmarks',
    [string]   $OutFile,
    [switch]   $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$stamp    = Get-Date -Format 'yyyyMMdd'
$bundle   = Join-Path $env:TEMP "runtime-async-repro-$stamp"
if (-not $OutFile) { $OutFile = Join-Path $Root "runtime-async-repro-$stamp.zip" }

if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle -Recurse -Force }
if ((Test-Path -LiteralPath $OutFile) -and -not $Force) {
    throw "$OutFile already exists. Use -Force to overwrite."
}

function New-Dir([string] $p) {
    if (-not (Test-Path -LiteralPath $p)) { New-Item -ItemType Directory -Path $p -Force | Out-Null }
    return $p
}

New-Dir $bundle | Out-Null
foreach ($d in 'commands', 'scripts', 'bits', 'results', 'manifests') { New-Dir (Join-Path $bundle $d) | Out-Null }

Write-Host "Building repro bundle" -ForegroundColor Cyan
Write-Host "  staging  $bundle"
Write-Host "  overlays $Overlays"

# ---------------------------------------------------------------------------------------
# 1. Command lines
# ---------------------------------------------------------------------------------------
# Absolute paths only make sense on the machine that produced them, so swap the two
# roots for placeholders that Run-Repro.ps1 expands on the far side.
function ConvertTo-Portable([string] $line) {
    $line = $line -replace [regex]::Escape($Root), '$BundleRoot'
    $line = $line -replace [regex]::Escape($BenchmarksRoot), '$BenchmarksRoot'
    $line = $line -replace [regex]::Escape((Join-Path $env:USERPROFILE '.dotnet\tools\crank.exe')), 'crank'
    return $line
}

$allCmds = [System.Collections.Generic.List[object]]::new()
foreach ($id in $RunId) {
    $mf = Join-Path $Root "logs\$id\manifest.jsonl"
    if (-not (Test-Path -LiteralPath $mf)) { Write-Warning "no manifest for $id"; continue }

    Copy-Item -LiteralPath $mf -Destination (Join-Path $bundle "manifests\$id.manifest.jsonl") -Force

    $recs = Get-Content -LiteralPath $mf | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json }
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# $id  --  $($recs.Count) crank invocations")
    $lines.Add("# Generated $(Get-Date -Format o)")
    $lines.Add('')
    foreach ($r in $recs) {
        $lines.Add("# $($r.Scenario) / $($r.Arm) / iteration $($r.Iteration)")
        $lines.Add((ConvertTo-Portable $r.Command))
        $lines.Add('')
        $allCmds.Add([pscustomobject]@{
            RunId = $id; Scenario = $r.Scenario; Arm = $r.Arm; Iteration = $r.Iteration
            Overlay = $r.Overlay; AppFlag = $r.AppFlag; ExitCode = $r.ExitCode
            Command = (ConvertTo-Portable $r.Command)
        })
    }
    Set-Content -LiteralPath (Join-Path $bundle "commands\$id.commands.txt") -Value $lines -Encoding UTF8
    Write-Host ("    {0,-16} {1,4} commands" -f $id, $recs.Count)
}

$allCmds | Export-Csv -LiteralPath (Join-Path $bundle 'commands\all-commands.csv') -NoTypeInformation
$allCmds | ForEach-Object { $_.Command } |
    Set-Content -LiteralPath (Join-Path $bundle 'commands\all-crank-commands.txt') -Encoding UTF8
Write-Host ("    {0,-16} {1,4} commands total" -f 'ALL', $allCmds.Count)

# A run folder accumulates every attempt, including ones that failed and ones that were later
# superseded (the rt-on arm was first run against an unpinned SDK, then re-run pinned). Replaying
# all of them would produce contradictory results, so also emit the canonical set: the last
# *successful* invocation per run/scenario/arm/iteration, which is what produced the shipped
# result JSONs. Run-Repro.ps1 uses this set by default.
$canonical = $allCmds |
    Where-Object { $_.ExitCode -eq 0 } |
    Group-Object RunId, Scenario, Arm, Iteration |
    ForEach-Object { $_.Group | Select-Object -Last 1 }

$canonical | Export-Csv -LiteralPath (Join-Path $bundle 'commands\canonical-commands.csv') -NoTypeInformation
$canonical | ForEach-Object { $_.Command } |
    Set-Content -LiteralPath (Join-Path $bundle 'commands\canonical-crank-commands.txt') -Encoding UTF8
Write-Host ("    {0,-16} {1,4} canonical ({2} superseded/failed)" -f 'CANONICAL', $canonical.Count, ($allCmds.Count - $canonical.Count))

# ---------------------------------------------------------------------------------------
# 2. Scripts
# ---------------------------------------------------------------------------------------
$scripts = @(
    'Invoke-RuntimeAsyncBenchmarks.ps1', 'New-LocalOverlay.ps1', 'New-MixedOverlay.ps1',
    'Compare-Results.ps1', 'Get-LatencyPercentiles.ps1', 'Test-AsyncOverlay.ps1',
    'Export-TeamsTables.ps1', 'Get-BcsBits.ps1'
)
foreach ($s in $scripts) {
    $p = Join-Path $Root $s
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $bundle "scripts\$s") -Force }
    else { Write-Warning "script not found: $s" }
}
Write-Host ("    {0,-16} {1,4} scripts" -f 'scripts', @(Get-ChildItem (Join-Path $bundle 'scripts')).Count)

# ---------------------------------------------------------------------------------------
# 3. Bits provenance + integrity
# ---------------------------------------------------------------------------------------
$runBits = @{ 'r3-gold-x64' = 'local-bits-r3-x64.json'; 'r3-cloud-arm64' = 'local-bits-r3-arm64.json'; 'cb200-4c' = 'local-bits-cb200-arm64.json' }
foreach ($f in @('local-bits-r3-x64.json', 'local-bits-r3-arm64.json')) {
    $p = Join-Path $Root $f
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $bundle "bits\$f") -Force }
}

# Only the rt-on arm was actually invoked with explicit --*Version pins; the round-3 arms
# predate the pinning and ran against whatever was "latest available" on their run date.
# Replaying those commands verbatim today would silently pick up a newer SDK - the exact
# trap documented in FINDINGS.md section 25.1 - so publish the per-run pin set and let
# Run-Repro.ps1 inject it into any command that lacks one.
$pinRows = [System.Collections.Generic.List[object]]::new()
foreach ($id in $RunId) {
    if (-not $runBits.ContainsKey($id)) { continue }
    $bp = Join-Path $Root $runBits[$id]
    if (-not (Test-Path -LiteralPath $bp)) { continue }
    $bj = Get-Content -LiteralPath $bp -Raw | ConvertFrom-Json
    $n = $bj.PSObject.Properties.Name
    $pinRows.Add([pscustomobject]@{
        RunId             = $id
        SdkVersion        = if ($n -contains 'SdkVersion')        { $bj.SdkVersion }        else { '' }
        RuntimeVersion    = if ($n -contains 'RuntimeVersion')    { $bj.RuntimeVersion }    else { '' }
        AspNetCoreVersion = if ($n -contains 'AspNetCoreVersion') { $bj.AspNetCoreVersion } else { '' }
        LoadSdkVersion    = if ($n -contains 'LoadSdkVersion')    { $bj.LoadSdkVersion }    else { '' }
    })
}
$pinRows | Export-Csv -LiteralPath (Join-Path $bundle 'commands\pins.csv') -NoTypeInformation
Write-Host ("    {0,-16} {1,4} run pin sets" -f 'pins', $pinRows.Count)

# Surfaced in the README so consumers can fetch the exact build artifacts themselves.
$runtimeSha = 'unknown'; $aspnetSha = 'unknown'
$bx = Join-Path $Root 'local-bits-r3-x64.json'
if (Test-Path -LiteralPath $bx) {
    $bxj = Get-Content -LiteralPath $bx -Raw | ConvertFrom-Json
    if ($bxj.PSObject.Properties.Name -contains 'RuntimeSha') { $runtimeSha = $bxj.RuntimeSha }
    if ($bxj.PSObject.Properties.Name -contains 'AspNetSha')  { $aspnetSha  = $bxj.AspNetSha }
}

# A hash of every file in every overlay lets a consumer prove their rebuild matches
# ours byte-for-byte, whether or not the binaries themselves ride along.
$hashRows = [System.Collections.Generic.List[object]]::new()
$overlayNames = @('r3-x64-off', 'r3-x64-rton', 'r3-x64-on', 'r3-arm64-off', 'r3-arm64-rton', 'r3-arm64-on')
foreach ($o in $overlayNames) {
    $dir = Join-Path $Root "overlay\$o"
    if (-not (Test-Path -LiteralPath $dir)) { continue }
    foreach ($f in Get-ChildItem -LiteralPath $dir -File) {
        $hashRows.Add([pscustomobject]@{
            Overlay = $o; File = $f.Name; Bytes = $f.Length
            SHA256 = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
        })
    }
}
$hashRows | Export-Csv -LiteralPath (Join-Path $bundle 'bits\overlay-file-hashes.csv') -NoTypeInformation
Write-Host ("    {0,-16} {1,4} file hashes" -f 'bits', $hashRows.Count)

# The names of the ASP.NET layer, so New-MixedOverlay.ps1 can re-derive the mixed
# overlay from the ON/OFF pair without the original staging folders.
$aspLib = Join-Path $Root 'local\aspnet-r3-x64-off\runtimes\linux-x64\lib\net11.0'
if (Test-Path -LiteralPath $aspLib) {
    @(Get-ChildItem -LiteralPath $aspLib -File |
        Where-Object { @('.dbg', '.pdb', '.a', '.lib', '.h') -notcontains $_.Extension } |
        Select-Object -ExpandProperty Name | Sort-Object) |
        Set-Content -LiteralPath (Join-Path $bundle 'bits\aspnet-layer-files.txt') -Encoding UTF8
}

# ---------------------------------------------------------------------------------------
# 4. Results
# ---------------------------------------------------------------------------------------
foreach ($id in $RunId) {
    $src = Join-Path $Root "out\$id"
    if (-not (Test-Path -LiteralPath $src)) { continue }
    $dst = New-Dir (Join-Path $bundle "results\$id")
    $n = 0
    foreach ($f in Get-ChildItem -LiteralPath $src -Filter *.json -File) {
        Copy-Item -LiteralPath $f.FullName -Destination $dst -Force; $n++
    }
    Write-Host ("    {0,-16} {1,4} result json" -f $id, $n)
}
foreach ($csv in Get-ChildItem (Join-Path $Root 'out') -Filter '*.csv' -File -ErrorAction SilentlyContinue) {
    Copy-Item -LiteralPath $csv.FullName -Destination (Join-Path $bundle 'results') -Force
}

# ---------------------------------------------------------------------------------------
# 5. Docs
# ---------------------------------------------------------------------------------------
foreach ($doc in @('FINDINGS.md', 'teams-results.html', 'teams-results.tsv')) {
    $p = Join-Path $Root $doc
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination (Join-Path $bundle $doc) -Force }
}

# ---------------------------------------------------------------------------------------
# 5b. Crank config + profile snapshot
# ---------------------------------------------------------------------------------------
# The scenario ymls pull their profile definitions from floating `main` URLs, so the set
# of available profiles is whatever upstream happens to be serving at run time - it is not
# pinned by the benchmarks checkout. That bit us for real: `cobalt-cloud-lin-al3` resolved
# on 2026-08-04 and had vanished by 2026-08-17. Vendor every local config the commands
# reference, and snapshot the remote imports, so a repro is not at upstream's mercy.
Write-Host '[5b] configs + profile snapshot'
$cfgDir = Join-Path $bundle 'config'
$snapDir = Join-Path $cfgDir 'imports-snapshot'
New-Item -ItemType Directory -Path $snapDir -Force | Out-Null

$cfgPaths = $allCmds |
    ForEach-Object { [regex]::Matches($_.Command, '--config\s+(\S+)') } |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique

$cfgRecs = [System.Collections.Generic.List[object]]::new()
$importUrls = [System.Collections.Generic.List[string]]::new()

foreach ($rel in $cfgPaths) {
    # Commands are already portable, so expand the placeholders back to real paths.
    $abs = $rel -replace [regex]::Escape('$BenchmarksRoot'), $BenchmarksRoot
    $abs = $abs -replace [regex]::Escape('$BundleRoot'), $Root
    if (-not (Test-Path -LiteralPath $abs)) { Write-Warning "config not found: $abs"; continue }

    # A config that lives in the workspace (not the benchmarks clone) is referenced as
    # $BundleRoot\<name>, so it must also sit at the bundle root or replay cannot find it.
    if ($rel -like '*$BundleRoot*') {
        $relPath = ($rel -replace [regex]::Escape('$BundleRoot'), '').TrimStart('\')
        $destAtRoot = Join-Path $bundle $relPath
        New-Item -ItemType Directory -Path (Split-Path $destAtRoot -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $abs -Destination $destAtRoot -Force
    }

    $leaf = Split-Path $abs -Leaf
    Copy-Item -LiteralPath $abs -Destination (Join-Path $cfgDir $leaf) -Force
    $cfgRecs.Add([pscustomobject]@{
        BundlePath = "config/$leaf"
        SourcePath = $rel
        Sha256     = (Get-FileHash -LiteralPath $abs -Algorithm SHA256).Hash
    })

    foreach ($m in [regex]::Matches((Get-Content -LiteralPath $abs -Raw), 'https?://\S+\.ya?ml')) {
        if (-not $importUrls.Contains($m.Value)) { $importUrls.Add($m.Value) }
    }
}

$snapRecs = [System.Collections.Generic.List[object]]::new()
foreach ($url in $importUrls) {
    $name = ($url -replace '^https?://', '' -replace '[\\/:*?"<>|]', '_')
    $dest = Join-Path $snapDir $name
    try {
        Invoke-WebRequest -Uri $url -UseBasicParsing -OutFile $dest -ErrorAction Stop
        $snapRecs.Add([pscustomobject]@{
            Url = $url; File = "config/imports-snapshot/$name"
            Sha256 = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash
            FetchedUtc = (Get-Date).ToUniversalTime().ToString('o')
        })
    } catch {
        Write-Warning "could not snapshot import $url : $($_.Exception.Message)"
        $snapRecs.Add([pscustomobject]@{ Url = $url; File = '(fetch failed)'; Sha256 = ''; FetchedUtc = '' })
    }
}

$cfgRecs  | Export-Csv -LiteralPath (Join-Path $cfgDir 'configs.csv') -NoTypeInformation
$snapRecs | Export-Csv -LiteralPath (Join-Path $cfgDir 'imports-snapshot.csv') -NoTypeInformation
Write-Host ("    {0,4} configs, {1,3} remote imports snapshotted" -f $cfgRecs.Count, $snapRecs.Count)

# ---------------------------------------------------------------------------------------
# 6. Overlays
# ---------------------------------------------------------------------------------------
if ($Overlays -ne 'none') {
    $want = if ($Overlays -eq 'all') { $overlayNames } else { $overlayNames | Where-Object { $_ -notlike '*-rton' } }
    # Named 'overlay' (not 'overlays') so it matches the --application.options.outputFiles
    # path already baked into the captured command lines; they then resolve with no rewriting.
    $odir = New-Dir (Join-Path $bundle 'overlay')
    foreach ($o in $want) {
        $src = Join-Path $Root "overlay\$o"
        if (-not (Test-Path -LiteralPath $src)) { continue }
        $dst = New-Dir (Join-Path $odir $o)
        Copy-Item -Path (Join-Path $src '*') -Destination $dst -Force
        $sz = (Get-ChildItem -LiteralPath $dst -File | Measure-Object Length -Sum).Sum / 1MB
        Write-Host ("    {0,-16} {1,7:N1} MB" -f $o, $sz)
    }
}

Set-Content -LiteralPath (Join-Path $bundle 'commands\Run-Repro.ps1') -Encoding UTF8 -Value @'
<#
Replays the exact crank invocations captured in this bundle.

The command lines were stored with $BundleRoot and $BenchmarksRoot placeholders;
this script expands them against your own checkout, so nothing is machine-specific.

Examples:
  .\Run-Repro.ps1 -BenchmarksRoot C:\src\benchmarks -RunId r3-gold-x64 -WhatIf
  .\Run-Repro.ps1 -BenchmarksRoot C:\src\benchmarks -Scenario json -Arm rt-on
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $BenchmarksRoot,
    [string] $BundleRoot = (Split-Path $PSScriptRoot -Parent),
    [string[]] $RunId,
    [string[]] $Scenario,
    [string[]] $Arm,
    [int[]]    $Iteration,
    # Replays write here by default so the bundle's reference results are never
    # overwritten. Pass -KeepOriginalPaths to honour the --json path as captured.
    [string]   $OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'repro-out'),
    [switch]   $KeepOriginalPaths,
    # Replay every recorded invocation, including failed and superseded ones. Off by default:
    # the run folders contain earlier attempts that were re-run (e.g. the rt-on arm before the
    # SDK was pinned), and replaying those alongside the canonical set gives contradictory data.
    [switch]   $IncludeSuperseded,
    # Replay commands exactly as captured, without injecting the recorded version pins.
    # Only useful if you deliberately want to measure against a newer toolchain.
    [switch]   $NoPin
)

$ErrorActionPreference = 'Stop'

$srcCsv = if ($IncludeSuperseded) { 'all-commands.csv' } else { 'canonical-commands.csv' }
$rows = Import-Csv (Join-Path $PSScriptRoot $srcCsv)
Write-Host "Using $srcCsv ($($rows.Count) invocations)"
if ($RunId)     { $rows = $rows | Where-Object { $RunId -contains $_.RunId } }
if ($Scenario)  { $rows = $rows | Where-Object { $Scenario -contains $_.Scenario } }
if ($Arm)       { $rows = $rows | Where-Object { $Arm -contains $_.Arm } }
if ($Iteration) { $rows = $rows | Where-Object { $Iteration -contains [int]$_.Iteration } }

if (-not $rows) { throw 'No commands matched the given filters.' }

if (-not (Get-Command crank -ErrorAction SilentlyContinue)) {
    throw "crank is not on PATH. Install with: dotnet tool install -g Microsoft.Crank.Controller --version '0.2.0-*'"
}

# crank resolves any unpinned version to "latest available" at run time, so a command that
# carried no pin when it was captured will drift the moment you replay it. Inject the pin set
# recorded for that run unless the command already has one.
$pinMap = @{}
$pinCsv = Join-Path $PSScriptRoot 'pins.csv'
if ((-not $NoPin) -and (Test-Path -LiteralPath $pinCsv)) {
    foreach ($p in Import-Csv $pinCsv) {
        $a = @()
        if ($p.SdkVersion)        { $a += @('--application.sdkVersion', $p.SdkVersion) }
        if ($p.RuntimeVersion)    { $a += @('--application.runtimeVersion', $p.RuntimeVersion) }
        if ($p.AspNetCoreVersion) { $a += @('--application.aspNetCoreVersion', $p.AspNetCoreVersion) }
        if ($p.LoadSdkVersion)    { $a += @('--load.sdkVersion', $p.LoadSdkVersion) }
        if ($a.Count) { $pinMap[$p.RunId] = ($a -join ' ') }
    }
}

Write-Host "Replaying $($rows.Count) crank invocation(s)" -ForegroundColor Cyan
if (-not $KeepOriginalPaths) { Write-Host "Results -> $OutDir" -ForegroundColor Cyan }
$i = 0
foreach ($r in $rows) {
    $i++
    $cmd = $r.Command.Replace('$BundleRoot', $BundleRoot).Replace('$BenchmarksRoot', $BenchmarksRoot)
    if ($pinMap.ContainsKey($r.RunId) -and $cmd -notmatch '--application\.sdkVersion') {
        $cmd = "$cmd $($pinMap[$r.RunId])"
    }
    if (-not $KeepOriginalPaths) {
        $dst = Join-Path $OutDir $r.RunId
        if (-not (Test-Path -LiteralPath $dst)) { New-Item -ItemType Directory -Path $dst -Force | Out-Null }
        $cmd = $cmd -replace '--json\s+\S+', ("--json `"$dst\$($r.Scenario)-$($r.Arm)-i$($r.Iteration).json`"")
    }
    Write-Host ""
    Write-Host ("[{0}/{1}] {2} / {3} / i{4}" -f $i, $rows.Count, $r.Scenario, $r.Arm, $r.Iteration) -ForegroundColor Cyan
    Write-Host $cmd -ForegroundColor DarkGray
    if ($PSCmdlet.ShouldProcess("$($r.Scenario) $($r.Arm) i$($r.Iteration)", 'crank')) {
        & $env:ComSpec /c $cmd
        if ($LASTEXITCODE -ne 0) { Write-Warning "exit code $LASTEXITCODE" }
    }
}
'@

# ---------------------------------------------------------------------------------------
# 7. README
# ---------------------------------------------------------------------------------------
$bitsX64 = $null
$bx = Join-Path $Root 'local-bits-r3-x64.json'
if (Test-Path -LiteralPath $bx) { $bitsX64 = Get-Content -LiteralPath $bx -Raw | ConvertFrom-Json }

$benchSha = try { (git -C $BenchmarksRoot rev-parse HEAD 2>$null) } catch { 'unknown' }
$crankVer = try {
    (dotnet tool list -g 2>$null | Select-String 'microsoft\.crank\.controller' |
        ForEach-Object { ($_ -split '\s+')[1] }) } catch { 'unknown' }

$armCounts = ($allCmds | Group-Object RunId, Arm |
    ForEach-Object { "| ``$(($_.Name -split ', ')[0])`` | ``$(($_.Name -split ', ')[1])`` | $($_.Count) |" }) -join "`n"

$readme = @"
# Runtime-async benchmark repro bundle

Everything needed to re-run, or just re-read, the runtime-async ASP.NET benchmark
experiment. Generated $(Get-Date -Format 'yyyy-MM-dd').

## What was measured

Runtime-async is enabled at three independent layers. This experiment separates them
by building the shared framework twice and swapping it underneath an unmodified app:

| Arm | Runtime | ASP.NET Core | App opts in | Isolates |
|---|---|---|---|---|
| ``all-off`` | OFF | OFF | no | baseline |
| ``rt-on``   | **ON** | OFF | no | the runtime layer alone |
| ``app-off`` | ON | **ON** | no | + the ASP.NET layer |
| ``all-on``  | ON | ON | **yes** | + the application layer |

Useful deltas:

* ``all-off -> all-on``   total cost of the feature
* ``all-off -> rt-on``    runtime layer alone
* ``rt-on   -> app-off``  ASP.NET layer alone
* ``app-off -> all-on``   cost of an app opting in on an already-async framework

The app opt-in is the csproj feature flag, passed by the harness as
``--application.buildArguments /p:Features=runtime-async=on``.

## Headline result

**The runtime layer is free; essentially the entire regression lives in ASP.NET Core.**
Throughput medians, percent change vs `all-off` (negative = slower):

| Arch | Scenario | full | **runtime** | **ASP.NET** | app | noise |
|---|---|---|---|---|---|---|
| x64 | plaintext | -2.78 | **+1.13** | **-3.79** | -0.08 | 5.56 |
| x64 | json | -3.48 | **+1.97** | **-5.13** | -0.24 | 1.82 |
| x64 | mvc | -7.52 | **-0.40** | **-4.87** | -2.40 | 6.16 |
| x64 | fortunes | -5.26 | **-0.01** | **-4.68** | -0.59 | 2.52 |
| x64 | fortunes_ef | -3.64 | **-0.53** | **-4.33** | +1.27 | 1.18 |
| x64 | multiple_queries | +0.86 | +1.43 | -0.61 | +0.05 | 2.50 |
| arm64 (cobalt100) | plaintext | -13.08 | **-2.65** | **-11.50** | +0.89 | 6.70 |
| arm64 (cobalt100) | json | -20.31 | **-2.15** | **-16.20** | -2.82 | 9.09 |
| arm64 (cobalt100) | mvc | -10.95 | **+1.41** | **-14.30** | +2.46 | 18.00 |
| **arm64 (CB200)** | plaintext | -3.08 | **+0.16** | **-3.20** | -0.04 | 2.31 |
| **arm64 (CB200)** | json | -5.42 | **-0.18** | **-4.75** | -0.53 | 5.39 |
| **arm64 (CB200)** | mvc | -5.41 | **-0.56** | **-4.43** | -0.47 | 4.85 |

Across all twelve combinations the runtime layer moves throughput between -2.65% and
+1.97%, and **in every case that magnitude is below the scenario's own iteration
noise**. `multiple_queries` is the control: db-bound, every layer inside noise.

> **The cobalt100 Arm64 rows are pod-specific, not an Arm64 property.** Re-running the
> identical bits on Cobalt 200, with the app confined to 4 cores so core count matches,
> brings Arm64 back to **-3.08% to -5.42%** -- the same range as x64. Cobalt 200 is also
> 58-125% faster per core. See §27; prefer the CB200 rows when quoting Arm64.

Full analysis, latency percentiles and caveats are in `FINDINGS.md`;
`teams-results.html` has the same tables formatted for pasting.

> **Compose layers multiplicatively, not additively.** `(1+rt)(1+asp)(1+app)-1` reproduces
> the full-stack number exactly; adding them disagrees by up to 0.85 pp. Note this
> identity telescopes, so it validates the arithmetic only -- it is not evidence about
> the feature. Also, `ASP.NET` is measured *given* an already-async runtime; enabling
> ASP.NET over a non-async runtime was not measured.

## Methodology

The full method is in ``FINDINGS.md``; these are the sections you need to check the
numbers rather than take them on trust:

| Section | Answers |
|---|---|
| §3, §24 | why four arms, and what each one isolates |
| §4, §15 | exactly how each framework flavor was acquired or built |
| §5, §16, §23 | **proof the ON/OFF bits really differ** — metadata counts, a control experiment against official bits, and an independent re-verification |
| §6 | how runs were executed: iteration counts, warmup, medians, how noise was computed |
| §25 | the two hazards that silently break a repro (version drift, floating profile imports) |
| §10, §11 | caveats, and which runs are missing and why |

The claim to attack first is that the overlay actually landed on the agent. Every run
was checked three ways: ``Test-AsyncOverlay.ps1`` counts the ``miAsync`` metadata flag
in the staged bits, ``-VerifyOverlay`` re-downloads marker files from the agent and
compares SHA256 after deployment, and the pre-overlay published size acts as a drift
canary (138,740 KB on x64, 144,025 KB on Arm64 — any change means the environment moved).

## Provenance

| Item | Value |
|---|---|
| dotnet/runtime commit | ``$(if ($bitsX64) { $bitsX64.RuntimeSha } else { 'see bits/' })`` |
| ...rebased onto main | ``$(if ($bitsX64 -and $bitsX64.PSObject.Properties.Name -contains 'RuntimeBaseSha') { $bitsX64.RuntimeBaseSha } else { 'n/a' })`` |
| dotnet/aspnetcore commit | ``$(if ($bitsX64) { $bitsX64.AspNetSha } else { 'see bits/' })`` |
| aspnet/benchmarks commit | ``$benchSha`` |
| crank controller | ``$crankVer`` |
| target framework | ``net11.0`` |

The runtime tree carries VSadov's PR #131177 rebased on top of the base commit above.

## Layout

``````
commands/     every crank invocation, verbatim; Run-Repro.ps1 replays them
              all-commands.csv        full history, including failed + superseded attempts
              canonical-commands.csv  the last successful attempt per run/scenario/arm/iteration
                                      -- this is the set that produced the shipped results
              pins.csv                the SDK/runtime/load versions each run must be pinned to
scripts/      the harness that produced them
bits/         overlay provenance + SHA256 of all $($hashRows.Count) staged files
config/       every local --config file the commands reference (scenario ymls +
              azure.profile.yml, which defines the Arm64 profile), plus
              imports-snapshot/ -- a copy of each remotely imported yml with its SHA256
results/      raw crank result JSON for every run, plus aggregated CSVs
manifests/    per-run manifest.jsonl (command, exit code, duration, overlay, flag)
overlay/      the shared-framework overlays themselves ($Overlays)
FINDINGS.md   the full written analysis
``````

> The overlay folder is deliberately named ``overlay`` (singular) because that is the
> path already embedded in the captured command lines, so they resolve unmodified.

### Two things that will silently ruin a repro

**1. Versions drift.** crank resolves every unpinned version to *latest available* at run time.
Only the ``rt-on`` commands were captured with explicit pins; the round-3 arms predate the
pinning and would pick up a newer SDK if replayed as-is. ``Run-Repro.ps1`` therefore reads
``commands/pins.csv`` and appends the recorded pin to any command that lacks one. Pass
``-NoPin`` only if you deliberately want a newer toolchain. If you build your own command
lines, add these by hand:

``````
# r3-gold-x64                                    # r3-cloud-arm64
--application.sdkVersion        11.0.100-rc.1.26402.101   # 11.0.100-rc.1.26404.112
--application.runtimeVersion    11.0.0-rc.1.26402.101     # 11.0.0-rc.1.26404.112
--application.aspNetCoreVersion 11.0.0-rc.1.26402.101     # 11.0.0-rc.1.26404.112
--load.sdkVersion               8.0.423                   # 8.0.423
``````

Sanity check: published size must come out at **138,740 KB** on x64 and **144,025 KB** on
Arm64. It is recorded before the overlay is applied, so if it differs your toolchain differs.

**2. Profiles are fetched from a floating ``main`` URL.** The scenario ymls import
``https://raw.githubusercontent.com/aspnet/Benchmarks/main/scenarios/aspnet.profiles.yml``,
so the available profile set is whatever upstream is serving *today*, not what your
benchmarks checkout contains. The Arm64 profile used here, ``cobalt-cloud-lin-al3``, is not
in that file at all -- it comes from ``build/azure.profile.yml``, which every Arm64 command
passes as a **second** ``--config``. Drop that argument and crank fails with
"Could not find a profile named ...". Both files are vendored under ``config/`` and the
remote imports are snapshotted under ``config/imports-snapshot/`` for comparison.

## Invocations captured

| Run | Arm | Count |
|---|---|---|
$armCounts

## Prerequisites

1. Windows with PowerShell 5.1+ (the harness is PowerShell; the agents are Linux).
2. crank controller:
   ``dotnet tool install -g Microsoft.Crank.Controller --version '0.2.0-*'``
3. A clone of ``https://github.com/aspnet/benchmarks`` at the commit above.
4. Access to the crank agent pools used by the profiles
   (``aspnet-gold-lin-relay``, ``cobalt-cloud-lin-al3-relay``). These are internal;
   substitute your own profile if you do not have them. **Relay profiles are required.**

## Reproducing

### Replay the exact commands

``````powershell
cd commands
.\Run-Repro.ps1 -BenchmarksRoot C:\src\benchmarks -RunId r3-gold-x64 -WhatIf   # preview
.\Run-Repro.ps1 -BenchmarksRoot C:\src\benchmarks -Scenario json -Arm rt-on
``````

``-WhatIf`` prints each command without running it. Filter with ``-RunId``,
``-Scenario``, ``-Arm``, ``-Iteration``. Replays write to ``repro-out/`` by default so
the bundle's reference results are preserved; pass ``-KeepOriginalPaths`` to override.

### Or drive the harness directly

``````powershell
.\scripts\Invoke-RuntimeAsyncBenchmarks.ps1 ``
    -Scenario json,plaintext,mvc -Arm all-off,rt-on,app-off,all-on ``
    -Iterations 5 -Profile aspnet-gold-lin ``
    -BitsJson .\bits\local-bits-r3-x64.json -VerifyOverlay
``````

``-VerifyOverlay`` re-downloads two marker files from the agent and compares their
SHA256 against the staged overlay, proving the framework swap actually landed.

$(if ($Overlays -eq 'source') {
@"
### Deriving the mixed rt-on overlay

This bundle ships the ON and OFF overlays. The ``rt-on`` overlay is a deterministic
mix of the two, so re-derive it rather than shipping a third copy:

``````powershell
.\scripts\New-MixedOverlay.ps1 -Arch x64   -Root .
.\scripts\New-MixedOverlay.ps1 -Arch arm64 -Root .
``````

It hash-verifies every file against its source layer, and you can check the result
against ``bits/overlay-file-hashes.csv``. Note it also needs ``local/aspnet-r3-<arch>-off``,
so if that staging folder was not included, rebuild from source instead.
"@
})

### Fetching the original build artifacts

The overlays were assembled from Build Cache Service artifacts, which are readable
anonymously. To pull the exact inputs rather than trusting the shipped overlays:

``````
https://pvscmdupload.z22.web.core.windows.net/builds/runtime/buildArtifacts/$runtimeSha/<config>/
https://pvscmdupload.z22.web.core.windows.net/builds/aspnetcore/buildArtifacts/$aspnetSha/<config>/
``````

where ``<config>`` is e.g. ``runtime_x64_linux`` / ``aspnetcore_x64_linux`` (or ``arm64``).
``scripts/Get-BcsBits.ps1`` automates the download. The SHAs are in
``bits/local-bits-r3-*.json``.

> **Erratum carried in the captured commands.** The ``all-off`` and ``app-off`` command lines
> record ``--property runtimeSha=70d6992b34776acddf85b56f780873cbfe92fc4b``, which does not
> exist in ``dotnet/runtime``. The real runtime commit is
> ``$runtimeSha`` (base ``ca4ed7d4a265c32e5240863c6b8ff45121339cc4``). The property is
> metadata only and had no effect on what was built or measured.

### Rebuilding the frameworks from source

``scripts/New-LocalOverlay.ps1`` stages the overlays from local ``dotnet/runtime`` and
``dotnet/aspnetcore`` worktrees. Both flavors are built from the *same* commits and differ
only by ``/p:UseRuntimeAsync``. Compare your rebuild against
``bits/overlay-file-hashes.csv``.

## Analysing

``````powershell
.\scripts\Compare-Results.ps1      -OutDir .\results -RunId r3-gold-x64 -SummaryCsv medians.csv
.\scripts\Get-LatencyPercentiles.ps1 -Base . -RunId r3-gold-x64 -Csv lat.csv
.\scripts\Test-AsyncOverlay.ps1 -OverlayPath .\overlay\r3-x64-on -ListAssemblies
``````

> **These two scripts take different path arguments, and it is easy to get wrong.**
> ``Compare-Results.ps1 -OutDir`` wants the **results root** (``.\results``).
> ``Get-LatencyPercentiles.ps1 -Base`` wants the folder **containing** ``results``
> (``.``) -- it appends the results directory itself. Passing ``-OutDir`` to the latter
> fails with "A parameter cannot be found"; passing ``.\results`` as ``-Base`` silently
> looks in ``.\results\results`` and reports "Parsed 0 runs".

Both are required: the scripts default to ```$PSScriptRoot\out``, which would resolve
inside ``scripts/`` once unpacked.

``Test-AsyncOverlay.ps1`` counts methods carrying ``MethodImplAttributes.Async``
(``miAsync = 0x2000``), the metadata marker the feature stamps on every async-compiled
method. Expect roughly 1,983 in an ON overlay, 884 in a mixed ``rt-on`` overlay, and
23 in an OFF overlay (those 23 are ``AsyncHelpers`` infrastructure, always present).

## Known issues

* **``--property runtimeSha=70d6992b…`` is a mislabel.** Runs recorded before
  2026-08-13 stamped a commit hash that does not exist in ``dotnet/runtime``. It is a
  label only and did not affect any binary; the verified commit is in the provenance
  table above and in ``bits/``. Later runs carry the corrected value.
* **OrchardCore has no ``all-off`` or ``rt-on`` arm.** It must publish
  framework-dependent, which resolves the shared framework from the agent's own dotnet
  install, so no overlay can apply to it.
* **Database scenarios fail on the Arm64 cloud pod.** Its database agent deadlocks
  (``Found deadlock on .../cobaltcloudlindb/jobs/N``) and crank then NREs in
  ``JobConnection.StopAsync``. Database results come from the x64 pool only.
* **Profile ``cores:`` metadata can be stale.** Derive the real core count from the
  results instead: ``benchmarks/cpu/raw`` divided by ``benchmarks/cpu``.
"@

Set-Content -LiteralPath (Join-Path $bundle 'README.md') -Value $readme -Encoding UTF8
Write-Host ("    {0,-16} {1,4} lines" -f 'README.md', ($readme -split "`n").Count)

Write-Host ""
Write-Host "Compressing..." -ForegroundColor Cyan
if (Test-Path -LiteralPath $OutFile) { Remove-Item -LiteralPath $OutFile -Force }
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $OutFile -CompressionLevel Optimal

$zipMb = (Get-Item -LiteralPath $OutFile).Length / 1MB
Write-Host ""
Write-Host ("Wrote {0}  ({1:N1} MB)" -f $OutFile, $zipMb) -ForegroundColor Green
Write-Host "Staging kept at $bundle"
