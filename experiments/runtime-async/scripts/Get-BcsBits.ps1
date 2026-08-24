<#
.SYNOPSIS
    Resolves the latest dotnet/runtime and dotnet/aspnetcore commit SHAs that have
    artifacts in the perf build cache service (BCS), downloads them, and extracts them.

.DESCRIPTION
    Runtime SHA comes from the BCS "latest builds" pointer:
        builds/runtime/latest/main/latestBuilds.json  ->  coreclr_x64_linux.CommitSha

    ASP.NET Core has no such pointer, so the latest SHA is resolved by walking recent
    commits on dotnet/aspnetcore main and probing BCS for a matching artifact.

    All BCS reads go through the anonymous static-website endpoint - no SAS/PAT/az login.

.EXAMPLE
    .\Get-BcsBits.ps1

.EXAMPLE
    .\Get-BcsBits.ps1 -RuntimeSha fdccdc6954791fcde7ffa2834d75930c0efa5456 -SkipDownload
#>
[CmdletBinding()]
param(
    [string] $RuntimeSha,
    [string] $AspNetSha,
    [string] $Branch = 'main',
    [string] $RuntimeConfig = 'coreclr_x64_linux',
    [string] $AspNetConfig = 'aspnetcore_x64_linux',
    [int]    $ProbeDepth = 25,
    [string] $CacheDir,
    [string] $BitsDir,
    [switch] $SkipDownload,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
if (-not $CacheDir) { $CacheDir = Join-Path $root 'cache' }
if (-not $BitsDir)  { $BitsDir  = Join-Path $root 'bits' }

$BcsBase = 'https://pvscmdupload.z22.web.core.windows.net/builds'

function New-Dir([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-RuntimeArtifactUrl([string] $Sha, [string] $Config) {
    # coreclr_x64_linux    -> BuildArtifacts_linux_x64_Release_coreclr.tar.gz
    # coreclr_x64_windows  -> BuildArtifacts_windows_x64_Release_coreclr.zip
    # coreclr_muslx64_linux-> BuildArtifacts_linux_musl_x64_Release_coreclr.tar.gz
    if ($Config -notmatch '^coreclr_(?<arch>[^_]+)_(?<os>[^_]+)$') {
        throw "Unrecognized runtime config key '$Config'."
    }
    $arch = $Matches.arch
    $os   = $Matches.os
    $ext  = if ($os -eq 'windows') { 'zip' } else { 'tar.gz' }
    $body = if ($arch -eq 'muslx64') { "linux_musl_x64" } else { "${os}_${arch}" }
    return "$BcsBase/runtime/buildArtifacts/$Sha/$Config/BuildArtifacts_${body}_Release_coreclr.$ext"
}

function Get-AspNetArtifactUrl([string] $Sha, [string] $Config) {
    if ($Config -notmatch '^aspnetcore_(?<arch>[^_]+)_(?<os>[^_]+)$') {
        throw "Unrecognized aspnetcore config key '$Config'."
    }
    $file = "BuildArtifacts_$($Matches.os)_$($Matches.arch)_Release_aspnetcore.nupkg"
    return "$BcsBase/aspnetcore/buildArtifacts/$Sha/$Config/$file"
}

function Get-HeaderInt64($Headers, [string] $Name) {
    if (-not $Headers.ContainsKey($Name)) { return [int64] 0 }
    # PowerShell 7 surfaces header values as string[]; Windows PowerShell as a bare string.
    $v = $Headers[$Name]
    if ($v -is [array]) { $v = $v[0] }
    [int64] $out = 0
    if ([int64]::TryParse([string]$v, [ref] $out)) { return $out }
    return [int64] 0
}

function Test-Url([string] $Url) {
    try {
        $r = Invoke-WebRequest -Uri $Url -Method Head -TimeoutSec 30 -ErrorAction Stop
        return [pscustomobject]@{ Ok = $true; Length = Get-HeaderInt64 $r.Headers 'Content-Length' }
    }
    catch { return [pscustomobject]@{ Ok = $false; Length = [int64] 0 } }
}

function Resolve-LatestRuntimeSha([string] $Branch, [string] $Config) {
    $url = "$BcsBase/runtime/latest/$Branch/latestBuilds.json"
    Write-Verbose "Fetching runtime latest pointer: $url"
    $resp = Invoke-WebRequest -Uri $url -TimeoutSec 60 -ErrorAction Stop

    # The endpoint serves application/octet-stream, so PowerShell may hand back raw bytes.
    $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string] $resp.Content }

    # Parsed as an untyped object on purpose: the live JSON carries config keys that the
    # checked-in C# LatestBuilds model does not know about.
    $json = $text | ConvertFrom-Json
    $entry = $json.PSObject.Properties | Where-Object { $_.Name -eq $Config } | Select-Object -First 1
    if (-not $entry) {
        $known = ($json.PSObject.Properties.Name | Where-Object { $_ -ne 'branch_name' }) -join ', '
        throw "Config '$Config' not present in latestBuilds.json. Available: $known"
    }
    $sha = $entry.Value.CommitSha
    if ([string]::IsNullOrWhiteSpace($sha)) { throw "latestBuilds.json has an empty CommitSha for '$Config'." }

    return [pscustomobject]@{ Sha = $sha; CommitTime = $entry.Value.CommitTime }
}

function Get-GitHubCommits([string] $Repo, [string] $Branch, [int] $Count) {
    $uri = "https://api.github.com/repos/$Repo/commits?sha=$Branch&per_page=$Count"

    # gh is preferred: it uses the user's token and dodges the 60 req/hr anonymous limit.
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($gh) {
        try {
            $out = & gh api "repos/$Repo/commits?sha=$Branch&per_page=$Count" --jq '.[] | [.sha, .commit.committer.date] | @tsv' 2>$null
            if ($LASTEXITCODE -eq 0 -and $out) {
                return @($out | Where-Object { $_ } | ForEach-Object {
                    $parts = $_ -split "`t"
                    [pscustomobject]@{ Sha = $parts[0]; Date = $parts[1] }
                })
            }
        }
        catch { Write-Verbose "gh api failed, falling back to anonymous REST: $_" }
    }

    $resp = Invoke-RestMethod -Uri $uri -Headers @{ 'User-Agent' = 'runtimeasync-bcs'; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 60
    return @($resp | ForEach-Object { [pscustomobject]@{ Sha = $_.sha; Date = $_.commit.committer.date } })
}

function Resolve-LatestAspNetSha([string] $Branch, [string] $Config, [int] $Depth) {
    Write-Verbose "Probing dotnet/aspnetcore $Branch for the newest commit with a BCS artifact"
    $commits = Get-GitHubCommits -Repo 'dotnet/aspnetcore' -Branch $Branch -Count $Depth
    if (-not $commits -or $commits.Count -eq 0) { throw "Could not list dotnet/aspnetcore commits for branch '$Branch'." }

    $i = 0
    foreach ($c in $commits) {
        $i++
        $url = Get-AspNetArtifactUrl -Sha $c.Sha -Config $Config
        $probe = Test-Url -Url $url
        if ($probe.Ok) {
            Write-Verbose "Hit on commit $i/$($commits.Count): $($c.Sha) ($($probe.Length) bytes)"
            return [pscustomobject]@{ Sha = $c.Sha; CommitTime = $c.Date }
        }
        Write-Verbose "No artifact for $($c.Sha) ($i/$($commits.Count))"
    }
    throw "No aspnetcore artifact found in the newest $Depth commits of dotnet/aspnetcore $Branch."
}

function Save-Artifact([string] $Url, [string] $Destination) {
    if ((Test-Path -LiteralPath $Destination) -and -not $Force) {
        $existing = (Get-Item -LiteralPath $Destination).Length
        $probe = Test-Url -Url $Url
        if ($probe.Ok -and $probe.Length -eq $existing) {
            Write-Host "  cached  $(Split-Path -Leaf $Destination) ($('{0:N0}' -f $existing) bytes)"
            return $Destination
        }
        Write-Host "  stale cache for $(Split-Path -Leaf $Destination), re-downloading"
    }

    Write-Host "  downloading $Url"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $old = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $Url -OutFile $Destination -TimeoutSec 3600 -ErrorAction Stop }
    finally { $ProgressPreference = $old }
    $sw.Stop()

    $len = (Get-Item -LiteralPath $Destination).Length
    Write-Host ("  done    {0} ({1:N0} bytes in {2:N0}s)" -f (Split-Path -Leaf $Destination), $len, $sw.Elapsed.TotalSeconds)
    return $Destination
}

function Expand-Artifact([string] $Archive, [string] $Destination) {
    if ((Test-Path -LiteralPath $Destination) -and -not $Force) {
        if ((Get-ChildItem -LiteralPath $Destination -Force | Select-Object -First 1)) {
            Write-Host "  extracted (cached) $Destination"
            return $Destination
        }
    }
    New-Dir $Destination | Out-Null

    Write-Host "  extracting $(Split-Path -Leaf $Archive) -> $Destination"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    if ($Archive -like '*.tar.gz' -or $Archive -like '*.tgz') {
        & tar -xzf $Archive -C $Destination
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE for $Archive" }
    }
    else {
        # .nupkg and .zip are both zip archives.
        $old = $ProgressPreference
        $ProgressPreference = 'SilentlyContinue'
        try { Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force }
        finally { $ProgressPreference = $old }
    }
    $sw.Stop()
    Write-Host ("  done    extracted in {0:N0}s" -f $sw.Elapsed.TotalSeconds)
    return $Destination
}

# Files we never want to ship to the agent: debug symbols and static libs roughly
# triple the payload without affecting what the app actually executes.
$script:OverlayExcludeExtensions = @('.dbg', '.pdb', '.a', '.lib', '.h')

function Copy-OverlayFiles([string] $Source, [string] $Destination, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Source)) { throw "Expected overlay source not found: $Source" }
    $files = Get-ChildItem -LiteralPath $Source -File |
        Where-Object { $script:OverlayExcludeExtensions -notcontains $_.Extension }
    foreach ($f in $files) { Copy-Item -LiteralPath $f.FullName -Destination $Destination -Force }
    $mb = ($files | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("  {0,-16} {1,4} files  {2,7:N1} MB" -f $Label, $files.Count, $mb)
    return $files.Count
}

<#
Builds a single flattened directory containing exactly the assemblies and native
libraries that should be dropped on top of the published app.

crank publishes self-contained by default, so the published folder is a flat layout of
managed assemblies plus native libs - which is why the overlay is flattened too.

Copy order matters: the ASP.NET shared framework layers on top of Microsoft.NETCore.App,
so aspnetcore is copied last and wins any name collision.
#>
function New-Overlay([string] $RuntimeRoot, [string] $AspNetRoot, [string] $Destination) {
    if ((Test-Path -LiteralPath $Destination) -and $Force) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    $existing = @()
    if (Test-Path -LiteralPath $Destination) { $existing = @(Get-ChildItem -LiteralPath $Destination -File) }
    if ($existing.Count -gt 0) {
        Write-Host "  overlay (cached) $Destination  [$($existing.Count) files]"
        return $Destination
    }
    New-Dir $Destination | Out-Null

    $runtimePack = Join-Path $RuntimeRoot 'microsoft.netcore.app.runtime.linux-x64\Release\runtimes\linux-x64'
    Copy-OverlayFiles -Source (Join-Path $runtimePack 'lib\net11.0') -Destination $Destination -Label 'runtime lib'    | Out-Null
    Copy-OverlayFiles -Source (Join-Path $runtimePack 'native')      -Destination $Destination -Label 'runtime native' | Out-Null
    Copy-OverlayFiles -Source (Join-Path $AspNetRoot 'runtimes\linux-x64\lib\net11.0') -Destination $Destination -Label 'aspnet lib' | Out-Null

    $all = Get-ChildItem -LiteralPath $Destination -File
    Write-Host ("  {0,-16} {1,4} files  {2,7:N1} MB" -f 'TOTAL', $all.Count, (($all | Measure-Object Length -Sum).Sum / 1MB))
    return $Destination
}

# ---------------------------------------------------------------------------

New-Dir $CacheDir | Out-Null
New-Dir $BitsDir  | Out-Null

Write-Host "Resolving build cache service bits (branch '$Branch')" -ForegroundColor Cyan

if ($RuntimeSha) {
    $runtime = [pscustomobject]@{ Sha = $RuntimeSha; CommitTime = $null }
    Write-Host "  runtime    $($runtime.Sha) (pinned)"
}
else {
    $runtime = Resolve-LatestRuntimeSha -Branch $Branch -Config $RuntimeConfig
    Write-Host "  runtime    $($runtime.Sha)  [$($runtime.CommitTime)]"
}

if ($AspNetSha) {
    $aspnet = [pscustomobject]@{ Sha = $AspNetSha; CommitTime = $null }
    Write-Host "  aspnetcore $($aspnet.Sha) (pinned)"
}
else {
    $aspnet = Resolve-LatestAspNetSha -Branch $Branch -Config $AspNetConfig -Depth $ProbeDepth
    Write-Host "  aspnetcore $($aspnet.Sha)  [$($aspnet.CommitTime)]"
}

$runtimeUrl = Get-RuntimeArtifactUrl -Sha $runtime.Sha -Config $RuntimeConfig
$aspnetUrl  = Get-AspNetArtifactUrl  -Sha $aspnet.Sha  -Config $AspNetConfig

$result = [ordered]@{
    RuntimeSha        = $runtime.Sha
    RuntimeCommitTime = $runtime.CommitTime
    RuntimeConfig     = $RuntimeConfig
    RuntimeUrl        = $runtimeUrl
    AspNetSha         = $aspnet.Sha
    AspNetCommitTime  = $aspnet.CommitTime
    AspNetConfig      = $AspNetConfig
    AspNetUrl         = $aspnetUrl
    RuntimeDir        = $null
    AspNetDir         = $null
    OverlayDir        = $null
}

if ($SkipDownload) {
    Write-Host "`n-SkipDownload specified; verifying URLs only" -ForegroundColor Yellow
    foreach ($pair in @(@('runtime', $runtimeUrl), @('aspnetcore', $aspnetUrl))) {
        $p = Test-Url -Url $pair[1]
        Write-Host ("  {0,-10} {1}  {2:N0} bytes" -f $pair[0], $(if ($p.Ok) { 'OK ' } else { 'MISSING' }), $p.Length)
        if (-not $p.Ok) { throw "Artifact not found: $($pair[1])" }
    }
    return [pscustomobject] $result
}

Write-Host "`nRuntime artifact" -ForegroundColor Cyan
$runtimeArchive = Save-Artifact -Url $runtimeUrl -Destination (Join-Path $CacheDir (Split-Path -Leaf ([uri]$runtimeUrl).AbsolutePath))
$result.RuntimeDir = Expand-Artifact -Archive $runtimeArchive -Destination (Join-Path $BitsDir "runtime-$($runtime.Sha)")

Write-Host "`nASP.NET Core artifact" -ForegroundColor Cyan
$aspnetArchive = Save-Artifact -Url $aspnetUrl -Destination (Join-Path $CacheDir "$($aspnet.Sha)-$(Split-Path -Leaf ([uri]$aspnetUrl).AbsolutePath)")
$result.AspNetDir = Expand-Artifact -Archive $aspnetArchive -Destination (Join-Path $BitsDir "aspnetcore-$($aspnet.Sha)")

Write-Host "`nStaging crank overlay" -ForegroundColor Cyan
$result.OverlayDir = New-Overlay -RuntimeRoot $result.RuntimeDir -AspNetRoot $result.AspNetDir `
    -Destination (Join-Path $BitsDir "overlay-$($runtime.Sha.Substring(0,12))-$($aspnet.Sha.Substring(0,12))")

$obj = [pscustomobject] $result
$obj | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'bits.json') -Encoding utf8
Write-Host "`nWrote $(Join-Path $root 'bits.json')" -ForegroundColor Green
return $obj
