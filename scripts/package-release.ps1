param([string]$PublishDirectory = 'artifacts/publish/win-x64', [string]$InstallerDirectory = 'artifacts/installer')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (![IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory = Join-Path $repoRoot $PublishDirectory }
if (![IO.Path]::IsPathRooted($InstallerDirectory)) { $InstallerDirectory = Join-Path $repoRoot $InstallerDirectory }
$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $PublishDirectory 'Senda.dll')).Version.ToString(3)
$releaseDirectory = Join-Path $repoRoot "artifacts/release/$version"
if (Test-Path -LiteralPath $releaseDirectory) { throw "La carpeta $releaseDirectory ya existe; conserva los artefactos anteriores." }
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$installer = Join-Path $InstallerDirectory "Senda-Setup-$version-win-x64.exe"
if (!(Test-Path -LiteralPath $installer)) { throw 'Genera el instalador antes de empaquetar la release.' }
Copy-Item -LiteralPath $installer -Destination $releaseDirectory
Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath (Join-Path $releaseDirectory "Senda-$version-Windows-x64.zip") -CompressionLevel Optimal
$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'No se pudo identificar el commit del build.' }
@{ version = $version; commit = $sourceCommit; sdk = (& dotnet --version).Trim(); platform = 'win-x64'; selfContained = $true } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseDirectory 'build-info.json') -Encoding utf8
$checksums = Get-ChildItem -LiteralPath $releaseDirectory -File | Sort-Object Name | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
}
[IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
Write-Output $releaseDirectory
