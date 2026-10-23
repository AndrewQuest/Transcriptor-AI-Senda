param([switch]$Installer, [string]$OutputDirectory = 'artifacts/publish/win-x64')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    dotnet restore Transcriptor.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Falló la restauración de dependencias.' }
    dotnet test tests/Transcriptor.Tests/Transcriptor.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas.' }
    dotnet test tests/Transcriptor.App.Tests/Transcriptor.App.Tests.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Fallaron las pruebas de la interfaz WPF.' }
    # Never mix runtime files left by an earlier .NET version with a new publication.
    $publishDirectory = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }))
    $publishRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/publish'))
    $publishPrefix = $publishRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$publishDirectory.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'La salida debe ser una subcarpeta de artifacts/publish.' }
    $directory = $publishDirectory
    while ($directory -and $directory -ne $repoRoot) {
        if ((Test-Path -LiteralPath $directory) -and ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'La salida de publicación no puede contener enlaces de directorio.'
        }
        $directory = Split-Path -Parent $directory
    }
    if (Test-Path -LiteralPath $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
    dotnet publish src/Transcriptor.App/Transcriptor.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación.' }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'Senda.runtimeconfig.json') -Raw | ConvertFrom-Json
    $desktopRuntime = $runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.WindowsDesktop.App'
    if (!$desktopRuntime) { throw 'Falta el runtime Windows autocontenido.' }
    $desktopAssembly = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $publishDirectory 'PresentationFramework.dll'))
    if ($desktopAssembly.Version.Major -ne ([Version]$desktopRuntime.version).Major) { throw 'La versión publicada de WPF no coincide con el runtime declarado.' }
    if ($Installer) {
        & (Join-Path $PSScriptRoot 'build-installer.ps1') -PublishDirectory $publishDirectory
    }
} finally { Pop-Location }
