param([string]$PublishDirectory = 'artifacts/publish/win-x64', [string]$OutputDirectory = 'artifacts/installer', [string]$CompilerPath = $env:INNO_SETUP_COMPILER)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (![IO.Path]::IsPathRooted($PublishDirectory)) { $PublishDirectory = Join-Path $repoRoot $PublishDirectory }
if (![IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $repoRoot $OutputDirectory }
if (!$CompilerPath) {
    foreach ($candidate in @((Join-Path $repoRoot 'artifacts/tools/inno-6.7.3/ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'))) {
        if (Test-Path -LiteralPath $candidate) { $CompilerPath = $candidate; break }
    }
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)) { throw 'Prepara Inno Setup con ./scripts/install-inno.ps1 o define INNO_SETUP_COMPILER.' }
$version = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $PublishDirectory 'Senda.dll')).Version.ToString(3)
& $CompilerPath "/DAppVersion=$version" "/DPublishDir=$PublishDirectory" "/DOutputDir=$OutputDirectory" (Join-Path $repoRoot 'installer/Senda.iss')
if ($LASTEXITCODE -ne 0) { throw 'Falló la compilación del instalador.' }
$installer = Join-Path $OutputDirectory "Senda-Setup-$version-win-x64.exe"
if (!(Test-Path -LiteralPath $installer)) { throw 'No se encontró el instalador esperado.' }
Write-Output $installer
