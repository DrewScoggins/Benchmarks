<#
.SYNOPSIS
    Runs the ASP.NET crank benchmarks across three runtime-async arms, N iterations each,
    capturing a full log and result JSON for every individual run.

.DESCRIPTION
    The runtime-async feature is enabled in three independent places:

      1. dotnet/runtime      src/libraries/Directory.Build.targets  (all shared-framework libs)
                             src/coreclr/.../System.Private.CoreLib.csproj
      2. dotnet/aspnetcore   Directory.Build.targets                (all shared-framework libs)
      3. the benchmark app   /p:Features=runtime-async=on

    The official build-cache-service bits have (1) and (2) ON, so toggling only (3) -- which
    is what the first round of this experiment did -- is NOT a true baseline. Scanning the
    shipped overlay confirms it: 1,983 methods carry MethodImplOptions.Async (0x2000).

    This script therefore runs three arms:

      all-off   locally built runtime + aspnetcore with /p:UseRuntimeAsync=false, app unflagged.
                Nothing anywhere is runtime-async.
      app-off   locally built runtime + aspnetcore with runtime-async ON, app unflagged.
      all-on    locally built runtime + aspnetcore with runtime-async ON, app flagged ON.

    All three overlays come from local builds of the SAME two commits, configured
    identically apart from UseRuntimeAsync, so the arms differ in exactly one variable.
    Comparing a locally built arm against an official BCS build would confound the result
    with build-environment differences.

.EXAMPLE
    .\Invoke-RuntimeAsyncBenchmarks.ps1 -Scenario json -Iterations 1 -DryRun

.EXAMPLE
    .\Invoke-RuntimeAsyncBenchmarks.ps1 -Iterations 5
#>
[CmdletBinding()]
param(
    [ValidateSet('json', 'plaintext', 'fortunes', 'orchard', 'mvc',
                 'fortunes_ef', 'multiple_queries', 'updates', 'fortunes_ef_mvc_https')]
    [string[]] $Scenario = @('json', 'plaintext', 'fortunes', 'orchard', 'mvc',
                             'fortunes_ef', 'multiple_queries', 'updates', 'fortunes_ef_mvc_https'),

    [ValidateSet('all-off', 'rt-on', 'app-off', 'all-on')]
    [string[]] $Arm = @('all-off', 'rt-on', 'app-off', 'all-on'),

    [ValidateRange(1, 25)]
    [int] $Iterations = 5,

    [string] $Profile = 'aspnet-gold-lin',

    # Additional --config files to pass to crank, for profiles that are not
    # defined in the scenario's own config (e.g. cobalt-cloud-* live in
    # build\azure.profile.yml rather than scenarios\aspnet.profiles.yml).
    [string[]] $ExtraConfig = @(),

    [switch] $NoRelay,

    [string] $Framework = 'net11.0',

    # Overlay built from the local runtime-async ON build.
    [string] $OverlayOn,

    # Overlay built from the local runtime-async OFF build.
    [string] $OverlayOff,

    # Mixed overlay: runtime layer from the ON build, ASP.NET layer from the OFF build.
    # Composed by New-MixedOverlay.ps1; isolates the runtime layer from the ASP.NET layer.
    [string] $OverlayRtOn,

    [string] $BitsJson = (Join-Path $PSScriptRoot 'local-bits.json'),

    [string] $BenchmarksRoot = 'D:\git\benchmarks',

    [string] $OutDir = (Join-Path $PSScriptRoot 'out'),

    [string] $LogDir = (Join-Path $PSScriptRoot 'logs'),

    # Reuse an existing run id (folder name) instead of minting a new one. Lets an
    # interrupted matrix be resumed into the same result set.
    [string] $RunId,

    # Skip any (scenario, arm, iteration) whose result JSON already exists.
    [switch] $Resume,

    [switch] $DryRun,

    # Verify on the first iteration of each (scenario, arm) that the overlay actually landed.
    [switch] $VerifyOverlay,

    [string[]] $CrankArgs = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------
# Scenarios
# ---------------------------------------------------------------------------------------
# Name        = the crank scenario name inside Config
# Extra       = additional crank arguments this scenario needs
# SkipOverlay = the app publishes framework-dependent, so an overlay in the published
#               folder cannot take effect
# Arms        = restrict which arms this scenario participates in

# DISARMED 2026-07-30 at user's request ("Don't do anything to delete anything").
#
# This previously read: $DbClean = @('--db.noClean', 'false')
#
# The intent was to stop DB runs leaving ~445 MB work folders behind. But for a *docker*
# job -- which the postgresql job is -- noClean has inverted-looking semantics in the
# agent (Startup.cs:2817-2827):
#     NoClean = true  -> docker rmi --force --no-prune   (image only, parents kept)
#     NoClean = false -> docker rmi --force              (image AND its parent layers)
# So passing 'false' makes crank delete MORE on the db machine, including shared base
# layers other benchmarks depend on. Empty until the disk story is confirmed.
$DbClean = @()

$ScenarioMap = [ordered]@{
    json                  = @{ Config = 'json.benchmarks.yml';      Name = 'json' }
    plaintext             = @{ Config = 'plaintext.benchmarks.yml'; Name = 'plaintext' }
    mvc                   = @{ Config = 'json.benchmarks.yml';      Name = 'mvc' }
    fortunes              = @{ Config = 'database.benchmarks.yml';  Name = 'fortunes';              Extra = $DbClean }
    fortunes_ef           = @{ Config = 'database.benchmarks.yml';  Name = 'fortunes_ef';           Extra = $DbClean }
    fortunes_ef_mvc_https = @{ Config = 'database.benchmarks.yml';  Name = 'fortunes_ef_mvc_https'; Extra = $DbClean }
    multiple_queries      = @{ Config = 'database.benchmarks.yml';  Name = 'multiple_queries';      Extra = $DbClean }
    updates               = @{ Config = 'database.benchmarks.yml';  Name = 'updates';               Extra = $DbClean }

    # OrchardCore needs two deviations, both isolated by control runs:
    #
    #  1. orchard.benchmarks.yml sets noGlobalJson: true, so the OrchardCore repo's own
    #     global.json wins and pins the 10.0 SDK, which cannot target net11.0.
    #  2. crank's default self-contained publish silently drops OrchardCore's module
    #     assets, so wwwroot is missing and every request throws ArgumentNullException.
    #     Reproduced with no overlay and no feature flag on both net10.0 and net11.0,
    #     so it is unrelated to this experiment.
    #
    # (2) forces a framework-dependent publish, which resolves the shared framework from
    # the agent's dotnet install rather than the published folder -- so no overlay can
    # apply and there is no way to give OrchardCore the runtime-async-off framework
    # without installing it on the agent. OrchardCore therefore only runs the two arms
    # that share the agent's stock framework.
    orchard               = @{
        Config      = 'orchard.benchmarks.yml'
        Name        = 'about-sqlite'
        Extra       = @('--application.noGlobalJson', 'false', '--application.selfContained', 'false')
        SkipOverlay = $true
        Arms        = @('app-off', 'all-on')
    }
}

# ---------------------------------------------------------------------------------------
# Arms
# ---------------------------------------------------------------------------------------
$ArmMap = [ordered]@{
    'all-off' = @{ Overlay = 'Off';  AppFlag = $false; Desc = 'runtime+aspnetcore+app all runtime-async OFF' }
    'rt-on'   = @{ Overlay = 'RtOn'; AppFlag = $false; Desc = 'runtime ON, aspnetcore OFF, app OFF' }
    'app-off' = @{ Overlay = 'On';   AppFlag = $false; Desc = 'runtime+aspnetcore ON, app OFF' }
    'all-on'  = @{ Overlay = 'On';   AppFlag = $true;  Desc = 'runtime+aspnetcore+app all ON' }
}

$script:OverlayMarkers = @('libclrjit.so', 'Microsoft.AspNetCore.Antiforgery.dll')

function Get-CrankPath {
    $cmd = Get-Command crank -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = Join-Path $env:USERPROFILE '.dotnet\tools\crank.exe'
    if (Test-Path -LiteralPath $fallback) { return $fallback }
    throw "crank was not found on PATH. Install with: dotnet tool install -g Microsoft.Crank.Controller --version '0.2.0-*'"
}

function Test-OverlayLanded([string] $ArtifactDir, [string] $OverlayDir) {
    $allMatched = $true
    foreach ($marker in $script:OverlayMarkers) {
        $expectedPath = Join-Path $OverlayDir $marker
        $actualPath = Join-Path $ArtifactDir $marker
        if (-not (Test-Path -LiteralPath $actualPath)) {
            Write-Host "  MISSING  $marker was not downloaded from the agent" -ForegroundColor Red
            $allMatched = $false
            continue
        }
        $expected = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath $actualPath -Algorithm SHA256).Hash
        if ($expected -eq $actual) {
            Write-Host "  MATCH    $marker  $($actual.Substring(0,16))" -ForegroundColor Green
        } else {
            Write-Host "  MISMATCH $marker  expected $($expected.Substring(0,16))  got $($actual.Substring(0,16))" -ForegroundColor Red
            $allMatched = $false
        }
    }
    return $allMatched
}

# ---------------------------------------------------------------------------------------
# Resolve overlays
# ---------------------------------------------------------------------------------------
$runtimeSha = 'unknown'
$aspnetSha = 'unknown'

if (Test-Path -LiteralPath $BitsJson) {
    $bits = Get-Content -LiteralPath $BitsJson -Raw | ConvertFrom-Json
    $names = $bits.PSObject.Properties.Name
    if (-not $OverlayOn -and $names -contains 'OverlayOn') { $OverlayOn = $bits.OverlayOn }
    if (-not $OverlayOff -and $names -contains 'OverlayOff') { $OverlayOff = $bits.OverlayOff }
    if (-not $OverlayRtOn -and $names -contains 'OverlayRtOn') { $OverlayRtOn = $bits.OverlayRtOn }
    if ($names -contains 'RuntimeSha') { $runtimeSha = $bits.RuntimeSha }
    if ($names -contains 'AspNetSha') { $aspnetSha = $bits.AspNetSha }
}

# Environment pinning. crank defaults every unpinned version to "latest available", so an
# arm run on a later date silently picks up a newer SDK/runtime/load client and is no
# longer comparable to earlier arms. Pin whatever the bits manifest declares.
$pinArgs = @()
if ($bits) {
    $pinMap = [ordered]@{
        SdkVersion         = '--application.sdkVersion'
        RuntimeVersion     = '--application.runtimeVersion'
        AspNetCoreVersion  = '--application.aspNetCoreVersion'
        LoadSdkVersion     = '--load.sdkVersion'
    }
    foreach ($field in $pinMap.Keys) {
        if ($names -notcontains $field) { continue }
        $val = $bits.$field
        if ([string]::IsNullOrWhiteSpace($val)) { continue }
        # An explicit -CrankArgs entry always wins over the manifest.
        if ($CrankArgs -contains $pinMap[$field]) { continue }
        $pinArgs += $pinMap[$field]
        $pinArgs += $val
    }
    if ($pinArgs.Count) {
        Write-Host "Pinning environment: $($pinArgs -join ' ')" -ForegroundColor DarkCyan
    } else {
        Write-Warning "No environment pin found in $BitsJson - crank will use 'latest available' versions, which drift over time and break cross-date comparisons."
    }
}

$overlayDirs = @{ On = $OverlayOn; Off = $OverlayOff; RtOn = $OverlayRtOn }

# Only demand the overlays the requested arms actually need.
$neededOverlays = @($Arm | ForEach-Object { $ArmMap[$_].Overlay } | Sort-Object -Unique)
foreach ($key in $neededOverlays) {
    $dir = $overlayDirs[$key]
    if (-not $dir) {
        throw "Arm(s) needing the '$key' overlay were requested but no path was supplied. Pass -Overlay$key or populate $BitsJson."
    }
    if (-not (Test-Path -LiteralPath $dir)) {
        throw "Overlay directory '$dir' does not exist."
    }
    if (@(Get-ChildItem -LiteralPath $dir -File).Count -eq 0) {
        throw "Overlay directory '$dir' is empty."
    }
}

$crank = Get-CrankPath
$useRelay = -not $NoRelay
if ($useRelay) { $profileName = "$Profile-relay" } else { $profileName = $Profile }

if ($RunId) { $stamp = $RunId } else { $stamp = Get-Date -Format 'yyyyMMdd-HHmmss' }

$runOut = Join-Path $OutDir $stamp
$runLog = Join-Path $LogDir $stamp
foreach ($d in @($runOut, $runLog)) {
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

$manifestPath = Join-Path $runLog 'manifest.jsonl'

Write-Host ''
Write-Host "run id       $stamp"
Write-Host "crank        $crank"
Write-Host "profile      $profileName"
Write-Host "framework    $Framework"
Write-Host "runtime sha  $runtimeSha"
Write-Host "aspnet sha   $aspnetSha"
foreach ($key in $neededOverlays) {
    $dir = $overlayDirs[$key]
    Write-Host "overlay $($key.PadRight(3))  $dir  [$(@(Get-ChildItem -LiteralPath $dir -File).Count) files]"
}
Write-Host "scenarios    $($Scenario -join ', ')"
Write-Host "arms         $($Arm -join ', ')"
Write-Host "iterations   $Iterations"
Write-Host "results      $runOut"
Write-Host "logs         $runLog"
Write-Host ''

$runs = [System.Collections.Generic.List[object]]::new()

foreach ($s in $Scenario) {
    $entry = $ScenarioMap[$s]

    $scenarioArms = $Arm
    if ($entry.Contains('Arms')) {
        $scenarioArms = @($Arm | Where-Object { $entry.Arms -contains $_ })
        $excluded = @($Arm | Where-Object { $entry.Arms -notcontains $_ })
        if ($excluded.Count -gt 0) {
            Write-Host "NOTE $s does not participate in arm(s): $($excluded -join ', ')" -ForegroundColor Yellow
        }
    }
    if ($scenarioArms.Count -eq 0) { continue }

    $configPath = Join-Path $BenchmarksRoot "scenarios\$($entry.Config)"
    if (-not (Test-Path -LiteralPath $configPath)) { throw "Scenario config not found: $configPath" }

    # Iteration outer, arm inner, so the arms being compared run adjacent in time and
    # share machine conditions as closely as possible.
    foreach ($i in 1..$Iterations) {
        foreach ($a in $scenarioArms) {
            $armDef = $ArmMap[$a]
            $tag = "$s-$a-i$i"
            $resultJson = Join-Path $runOut "$tag.json"
            $logFile = Join-Path $runLog "$tag.log"

            if ($Resume -and (Test-Path -LiteralPath $resultJson)) {
                Write-Host "SKIP $tag (result already exists)" -ForegroundColor DarkGray
                $runs.Add([pscustomobject]@{ Scenario = $s; Arm = $a; Iteration = $i; ExitCode = 0; Seconds = 0; Json = $resultJson })
                continue
            }

            $overlayDir = $overlayDirs[$armDef.Overlay]
            $overlayApplies = -not $entry.Contains('SkipOverlay')

            $cargs = @(
                '--config', $configPath
            )
            foreach ($ec in $ExtraConfig) { $cargs += @('--config', $ec) }
            $cargs += @(
                '--scenario', $entry.Name
                '--profile', $profileName
                '--application.framework', $Framework
            )

            if ($entry.Contains('Extra')) { $cargs += $entry.Extra }
            if ($useRelay) { $cargs += '--relay' }

            if ($overlayApplies) {
                # Explicit glob: crank splits the value into GetDirectoryName/GetFileName
                # and calls Directory.GetFiles, so a bare directory matches nothing.
                $cargs += @('--application.options.outputFiles', (Join-Path $overlayDir '*'))
            }

            if ($armDef.AppFlag) {
                $cargs += @('--application.buildArguments', '/p:Features=runtime-async=on')
            }

            $verifyThis = $VerifyOverlay -and $overlayApplies -and ($i -eq 1)
            $artifactDir = Join-Path $runLog "artifacts-$s-$a"
            if ($verifyThis) {
                if (-not (Test-Path -LiteralPath $artifactDir)) {
                    New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
                }
                $cargs += @('--application.options.downloadFilesOutput', $artifactDir)
                foreach ($marker in $script:OverlayMarkers) {
                    $cargs += @('--application.options.downloadFiles', $marker)
                }
            }

            $cargs += @(
                '--json', $resultJson
                '--description', "$s $a i$i"
                '--property', "scenario=$s"
                '--property', "arm=$a"
                '--property', "iteration=$i"
                '--property', "overlay=$($armDef.Overlay)"
                '--property', "appFlag=$($armDef.AppFlag)"
                '--property', "runtimeSha=$runtimeSha"
                '--property', "aspnetSha=$aspnetSha"
                '--property', "profile=$profileName"
                '--property', "runId=$stamp"
            )

            # Manifest pins first, then explicit -CrankArgs so the caller can override.
            $cargs += $pinArgs
            $cargs += $CrankArgs

            $quoted = $cargs | ForEach-Object { if ($_ -match '[\s;]') { '"' + $_ + '"' } else { $_ } }
            $cmdLine = "$crank $($quoted -join ' ')"

            Write-Host ('=' * 100)
            Write-Host "RUN  $tag   [$($armDef.Desc)]"
            Write-Host ('=' * 100)
            Write-Host $cmdLine
            Write-Host ''

            if ($DryRun) {
                $runs.Add([pscustomobject]@{ Scenario = $s; Arm = $a; Iteration = $i; ExitCode = $null; Seconds = 0; Json = $resultJson })
                continue
            }

            if ($overlayApplies) { $overlayNote = $overlayDir } else { $overlayNote = '(n/a, framework-dependent publish)' }
            $header = @(
                "# run      $tag"
                "# runId    $stamp"
                "# scenario $s  ($($entry.Config) :: $($entry.Name))"
                "# arm      $a  -- $($armDef.Desc)"
                "# overlay  $overlayNote"
                "# appFlag  $($armDef.AppFlag)"
                "# started  $(Get-Date -Format o)"
                "# command  $cmdLine"
                ''
            )
            Set-Content -LiteralPath $logFile -Value $header -Encoding utf8

            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            & $crank @cargs 2>&1 | Tee-Object -FilePath $logFile -Append
            $exit = $LASTEXITCODE
            $sw.Stop()

            Add-Content -LiteralPath $logFile -Encoding utf8 -Value @(
                ''
                "# finished $(Get-Date -Format o)"
                "# exitCode $exit"
                "# seconds  $([int]$sw.Elapsed.TotalSeconds)"
            )

            Write-Host ''
            if ($exit -eq 0) {
                Write-Host "OK   $tag  in $([int]$sw.Elapsed.TotalSeconds)s" -ForegroundColor Green
            } else {
                Write-Host "FAIL $tag  exit $exit  in $([int]$sw.Elapsed.TotalSeconds)s" -ForegroundColor Red
            }

            if ($verifyThis -and $exit -eq 0) {
                Write-Host "Overlay verification for $tag"
                if (-not (Test-OverlayLanded -ArtifactDir $artifactDir -OverlayDir $overlayDir)) {
                    Write-Host "  The published app is NOT running on the expected bits." -ForegroundColor Red
                }
            }
            Write-Host ''

            $record = [pscustomobject]@{
                RunId    = $stamp; Scenario = $s; Arm = $a; Iteration = $i
                ExitCode = $exit; Seconds = [int]$sw.Elapsed.TotalSeconds
                Json     = $resultJson; Log = $logFile; Command = $cmdLine
                Overlay  = $armDef.Overlay; AppFlag = $armDef.AppFlag
            }
            Add-Content -LiteralPath $manifestPath -Encoding utf8 -Value ($record | ConvertTo-Json -Compress -Depth 5)

            $runs.Add([pscustomobject]@{ Scenario = $s; Arm = $a; Iteration = $i; ExitCode = $exit; Seconds = [int]$sw.Elapsed.TotalSeconds; Json = $resultJson })
        }
    }
}

Write-Host ('=' * 100)
Write-Host 'SUMMARY'
Write-Host ('=' * 100)
$runs | Format-Table -AutoSize | Out-String | Write-Host

if (-not $DryRun) {
    Write-Host "run id:  $stamp"
    Write-Host "results: $runOut"
    Write-Host "logs:    $runLog"
    $failed = @($runs | Where-Object { $_.ExitCode -ne 0 })
    if ($failed.Count -gt 0) {
        Write-Host "$($failed.Count) of $($runs.Count) runs FAILED" -ForegroundColor Red
        exit 1
    }
    Write-Host "all $($runs.Count) runs succeeded" -ForegroundColor Green
}
