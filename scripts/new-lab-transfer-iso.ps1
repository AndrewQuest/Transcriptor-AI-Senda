param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$OutputPath,
    [ValidatePattern('^[A-Z0-9_]{1,32}$')][string]$VolumeName = 'SENDA_TESTS')
$ErrorActionPreference = 'Stop'
$labRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/linux-lab')).TrimEnd('\')
function Resolve-OwnedPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (!$resolved.StartsWith($labRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Transfer paths must stay inside artifacts/linux-lab.' }
    $current = $resolved
    while ($current.StartsWith($labRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse points are not allowed in transfer paths.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $resolved
}
$source = Resolve-OwnedPath $SourceDirectory
$output = Resolve-OwnedPath $OutputPath
if (!(Test-Path -LiteralPath $source -PathType Container)) { throw 'The transfer source directory does not exist.' }
if ([IO.Path]::GetExtension($output) -ne '.iso' -or (Test-Path -LiteralPath $output)) { throw 'Choose a new .iso file inside the lab.' }
if (Get-ChildItem -LiteralPath $source -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw 'Transfer contents must not contain reparse points.' }
if (!(Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($output)))) { throw 'Create the output directory inside the lab first.' }
if (!('SendaLab.IsoStream' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
namespace SendaLab {
    public static class IsoStream {
        public static void Write(object source, string path) {
            var input = (IStream)source;
            var buffer = new byte[1024 * 1024];
            var count = Marshal.AllocHGlobal(sizeof(int));
            try {
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) {
                    while (true) {
                        input.Read(buffer, buffer.Length, count);
                        var read = Marshal.ReadInt32(count);
                        if (read == 0) break;
                        output.Write(buffer, 0, read);
                    }
                    output.Flush(true);
                }
            } finally { Marshal.FreeHGlobal(count); }
        }
    }
}
'@
}
# Windows' built-in image writer; no shared folder, clipboard, or host drive is exposed.
$image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
try {
    $image.FileSystemsToCreate = 4 # UDF preserves Linux package file names.
    $image.VolumeName = $VolumeName
    $image.Root.AddTree($source, $false)
    $result = $image.CreateResultImage()
    [SendaLab.IsoStream]::Write($result.ImageStream, $output)
    Get-FileHash -LiteralPath $output -Algorithm SHA256
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($image) }
