param([Parameter(Mandatory = $true)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
# This test intentionally installs and uninstalls Senda. Run only in an isolated CI account.
if ($env:CI -ne 'true' -or !$env:RUNNER_TEMP) { throw 'Ejecuta esta comprobación únicamente en un runner CI aislado.' }
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SendaWindowProbe {
    private delegate bool Visitor(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Visitor visitor, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    public static string FindTitle(uint processId) {
        string found = "";
        EnumWindows((window, parameter) => {
            GetWindowThreadProcessId(window, out uint owner);
            if (owner != processId) return true;
            var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "Senda — Dictado local" || title.ToString() == "Senda — Local dictation") {
                found = title.ToString(); return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
$installDirectory = Join-Path $env:RUNNER_TEMP 'Senda-install-smoke'
$log = Join-Path $env:RUNNER_TEMP 'Senda-install.log'
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', '/NOICONS', '/LANG=english', ('/DIR="' + $installDirectory + '"'), ('/LOG="' + $log + '"'))
$setup = Start-Process -FilePath $InstallerPath -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($setup.ExitCode -ne 0) { throw ('Falló la instalación silenciosa: ' + $setup.ExitCode) }
$app = $null
try {
    foreach ($relative in @('Senda.exe', 'Senda.dll', 'PresentationFramework.dll', 'runtimes/vulkan/win-x64/whisper.dll', 'licenses/Whisper.net.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $installDirectory $relative))) { throw "Falta el archivo instalado: $relative" }
    }
    $app = Start-Process -FilePath (Join-Path $installDirectory 'Senda.exe') -WorkingDirectory $installDirectory -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 500
        $app.Refresh()
        if ($app.HasExited) { throw ('La aplicación instalada se cerró al iniciar: ' + $app.ExitCode) }
        $title = [SendaWindowProbe]::FindTitle($app.Id)
    } while (!$title -and [DateTime]::UtcNow -lt $deadline)
    if (!$title -or !$app.Responding) {
        throw 'La aplicación instalada no abrió la ventana esperada.'
    }
    Write-Output ('Arranque instalado aprobado: ' + $title)
} finally {
    if ($app -and !$app.HasExited) { Stop-Process -Id $app.Id -Force; $app.WaitForExit() }
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) {
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) { throw ('Falló la desinstalación: ' + $uninstall.ExitCode) }
        if (Test-Path -LiteralPath (Join-Path $installDirectory 'Senda.exe')) { throw 'El ejecutable sigue instalado tras desinstalar.' }
    }
}
