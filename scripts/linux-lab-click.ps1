param([Parameter(Mandatory)][ValidateSet('gnome','kde')][string]$Desktop, [Parameter(Mandatory)][ValidateRange(0,16383)][int]$X, [Parameter(Mandatory)][ValidateRange(0,16383)][int]$Y)
$ErrorActionPreference='Stop'
$labRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/linux-lab'))
$record=Get-Content -LiteralPath (Join-Path $labRoot 'lab.json') -Raw | ConvertFrom-Json -AsHashtable
if ($record.id -ne 'Senda-linux-lab-v1' -or $record.root -ne $labRoot -or !$record.vms.ContainsKey($Desktop)) { throw 'Unknown lab identity.' }
$vm=$record.vms[$Desktop]
$previousHome=$env:VBOX_USER_HOME
$env:VBOX_USER_HOME=Join-Path $labRoot 'vbox-home'
$session=$null
$locked=$false
try {
    # VirtualBox's guest-input API only; this never moves or clicks the Windows pointer.
    $api=New-Object -ComObject VirtualBox.VirtualBox
    $machine=$api.FindMachine($vm.uuid)
    if ($machine.Id -ne $vm.uuid -or [IO.Path]::GetFullPath($machine.SettingsFilePath) -ne $vm.config -or $machine.State -ne 6) { throw 'Expected the owned running VM.' }
    $session=New-Object -ComObject VirtualBox.Session
    $machine.LockMachine($session,1) # Shared; never changes the VM's configuration.
    $locked=$true
    $mouse=$session.Console.Mouse
    # Let the guest consume pointer motion before pressing. Combining both in
    # one event can begin a title-bar drag on a slow virtual desktop.
    $mouse.PutMouseEventAbsolute($X+1,$Y+1,0,0,0)
    Start-Sleep -Milliseconds 180
    $mouse.PutMouseEventAbsolute($X+1,$Y+1,0,0,1)
    Start-Sleep -Milliseconds 100
    $mouse.PutMouseEventAbsolute($X+1,$Y+1,0,0,0)
} finally {
    if ($locked) { $session.UnlockMachine() }
    $env:VBOX_USER_HOME=$previousHome
}
