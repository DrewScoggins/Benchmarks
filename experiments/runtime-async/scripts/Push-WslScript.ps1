# Copy a script from the session workspace into a WSL distro as LF and make it executable.
param(
    [Parameter(Mandatory)] [string[]] $Name,
    [string] $Distro = 'Ubuntu-24.04',
    [string] $Dest   = '/home/anscoggi/ra/wsl'
)
$ErrorActionPreference = 'Stop'
$base = Join-Path $PSScriptRoot 'wsl'

# Overwriting a .sh file while bash is executing it corrupts the running shell: bash
# re-reads the script by byte offset, so it resumes mid-token in the new content and
# fails with nonsense like "line 1: Error: command not found". Block only the specific
# files that are currently executing, so unrelated scripts can still be pushed.
# The [.] bracket keeps the regex from matching the pgrep invocation's own command line.
$running = @(wsl -d $Distro -- bash -c "pgrep -af 'ra/wsl/.*[.]sh' 2>/dev/null || true")
$busy = @($Name | Where-Object { $n = $_; $running | Where-Object { $_ -match [regex]::Escape($n) } })
if ($busy.Count) {
    throw "Refusing to push (currently executing in ${Distro}): $($busy -join ', ')`n$($running -join "`n")"
}

wsl -d $Distro -- mkdir -p $Dest | Out-Null
foreach ($n in $Name) {
    $src = Join-Path $base $n
    if (-not (Test-Path -LiteralPath $src)) { throw "missing $src" }
    [IO.File]::WriteAllText($src, ([IO.File]::ReadAllText($src) -replace "`r`n", "`n"))
    $wp = (wsl -d $Distro wslpath -a ($src -replace '\\', '/')).Trim()
    wsl -d $Distro -- cp "$wp" "$Dest/$n"
    if ($n -like '*.sh') { wsl -d $Distro -- chmod +x "$Dest/$n" }
    $kind = (wsl -d $Distro -- file "$Dest/$n")
    Write-Host "  $n -> $Dest/$n"
    if ($kind -match 'CRLF') { throw "CRLF survived in $n" }
}
