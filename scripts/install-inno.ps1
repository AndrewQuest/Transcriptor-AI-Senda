$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolDirectory = Join-Path $repoRoot 'artifacts/tools/inno-6.7.3'
$compiler = Join-Path $toolDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }
$downloadDirectory = Join-Path $repoRoot 'artifacts/tools/downloads'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$setup = Join-Path $downloadDirectory 'innosetup-6.7.3.exe'
$expectedHash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
if (!(Test-Path -LiteralPath $setup) -or (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $expectedHash) {
    Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $setup
}
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $expectedHash) { throw 'La verificación de Inno Setup falló.' }
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/CURRENTUSER', '/NOICONS', ('/DIR="' + $toolDirectory + '"'))
$process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw ('No se pudo preparar Inno Setup: ' + $process.ExitCode) }
Write-Output $compiler
