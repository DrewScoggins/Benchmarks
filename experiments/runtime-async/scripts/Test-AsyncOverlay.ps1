[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OverlayPath,
    [switch] $ListAssemblies,
    [int]    $Top = 15
)

# Counts methods carrying MethodImplAttributes.Async (miAsync = 0x2000), the metadata
# marker the runtime-async feature stamps on every async-compiled method.
# Verified against src/coreclr/inc/corhdr.h:650 in the round-3 runtime worktree.

Add-Type -AssemblyName System.Reflection.Metadata -ErrorAction SilentlyContinue | Out-Null

$MiAsync = 0x2000
$results = New-Object System.Collections.Generic.List[object]

Get-ChildItem $OverlayPath -Recurse -File -Include *.dll | ForEach-Object {
    $file = $_
    $fs = $null; $pe = $null
    try {
        $fs = [System.IO.File]::OpenRead($file.FullName)
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
        if (-not $pe.HasMetadata) { return }
        $mr = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)

        $async = 0; $total = 0
        foreach ($h in $mr.MethodDefinitions) {
            $md = $mr.GetMethodDefinition($h)
            $total++
            if (([int]$md.ImplAttributes -band $MiAsync) -ne 0) { $async++ }
        }

        $results.Add([pscustomobject]@{
            Assembly     = $file.Name
            RelPath      = $file.FullName.Substring($OverlayPath.Length).TrimStart('\')
            AsyncMethods = $async
            TotalMethods = $total
        })
    }
    catch { }
    finally {
        if ($pe) { $pe.Dispose() }
        if ($fs) { $fs.Dispose() }
    }
}

$withAsync = $results | Where-Object AsyncMethods -gt 0

[pscustomobject]@{
    Overlay            = $OverlayPath
    ManagedAssemblies  = $results.Count
    AssembliesWithAsync= $withAsync.Count
    TotalAsyncMethods  = ($results | Measure-Object AsyncMethods -Sum).Sum
    TotalMethods       = ($results | Measure-Object TotalMethods -Sum).Sum
} | Format-List

if ($ListAssemblies -and $withAsync) {
    $withAsync | Sort-Object AsyncMethods -Descending |
        Select-Object -First $Top Assembly, AsyncMethods, TotalMethods |
        Format-Table -AutoSize
}

$results | Sort-Object Assembly
