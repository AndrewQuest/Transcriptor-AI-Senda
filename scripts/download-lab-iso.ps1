param([ValidateSet('gnome','kde')][string]$Desktop = 'gnome')
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$root=[IO.Path]::GetFullPath((Join-Path $repo 'artifacts/linux-lab/downloads'))
$asset=(Get-Content (Join-Path $repo 'lab/assets.json') -Raw | ConvertFrom-Json -AsHashtable)[$Desktop]
$target=Join-Path $root $asset.file
if((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $asset.sha256){Write-Output 'ISO already verified';return}
$size=if($Desktop -eq 'gnome'){6482409472L}else{5072990208L}
$partial=$target+'.partial'
$prefix=if(Test-Path -LiteralPath $partial){(Get-Item -LiteralPath $partial).Length}else{0L}
if($prefix -gt $size){throw 'Partial ISO exceeds expected size'}
$chunkSize=[long][Math]::Ceiling(($size-$prefix)/12d)
$chunks=@(0..11 | ForEach-Object {
    $start=$prefix+$_*$chunkSize; $end=[Math]::Min($size-1,$start+$chunkSize-1)
    if($start -le $end){[pscustomobject]@{Index=$_;Start=$start;End=$end;Path=($target+'.part'+$_)}}
})
$url=$asset.url
$chunks | ForEach-Object -Parallel {
    $part=$_
    & curl.exe --fail --location --silent --show-error --retry 3 --connect-timeout 20 --max-time 1800 --range "$($part.Start)-$($part.End)" --output $part.Path $using:url
    if($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $part.Path).Length -ne $part.End-$part.Start+1){throw "Failed ISO segment $($part.Index)"}
    Write-Output "Downloaded ISO segment $($part.Index)"
} -ThrottleLimit 12
$output=[IO.File]::Open($partial,[IO.FileMode]::Append,[IO.FileAccess]::Write)
try {foreach($chunk in ($chunks | Sort-Object Index)){
    $input=[IO.File]::OpenRead($chunk.Path);try{$input.CopyTo($output)}finally{$input.Dispose()}
}}finally{$output.Dispose()}
if((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $asset.sha256){throw 'ISO checksum mismatch; no unverified ISO will be used'}
Move-Item -LiteralPath $partial -Destination $target -Force
# The explicit chunk paths above are children of the fixed lab downloads directory.
foreach($chunk in $chunks){Remove-Item -LiteralPath $chunk.Path}
Write-Output "Verified $Desktop ISO"
