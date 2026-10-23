param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Download','InstallTool','Status','Create','Start','Stop','Checkpoint','Reset','Attach','Remove','UninstallTool')][string]$Action,
    [ValidateSet('gnome','kde')][string]$Desktop = 'gnome',
    [ValidateSet('virtualbox','gnome','kde','all')][string]$Asset = 'all',
    [string]$IsoPath,
    [switch]$Online,
    [switch]$Microphone,
    [switch]$ConfirmRemoval
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$lab = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/linux-lab'))
$marker = Join-Path $lab 'lab.json'
$assets = Get-Content (Join-Path $repo 'lab/assets.json') -Raw | ConvertFrom-Json -AsHashtable
$compiler = Join-Path $env:ProgramFiles 'Oracle/VirtualBox/VBoxManage.exe'

function Assert-LabPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $lab -and !$full.StartsWith($lab + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Path outside the Linux lab: $full" }
    $cursor = $full
    while ($cursor -and $cursor -ne $repo) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked lab path: $cursor" }
        $cursor = Split-Path -Parent $cursor
    }
    return $full
}
function Save-Ledger($Ledger) {
    $temporary = Join-Path $lab ('ledger-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $Ledger | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $marker -Force
}
function Read-Ledger {
    $record = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json -AsHashtable
    if ($record.id -ne 'Senda-linux-lab-v1' -or $record.root -ne $lab) { throw 'The lab ownership marker does not match this directory.' }
    return $record
}
function Invoke-VBox([string[]]$Arguments) {
    if (!(Test-Path -LiteralPath $compiler)) { throw 'VirtualBox is not installed. Run InstallTool first.' }
    $output = & $compiler @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output -join "`n") }
    return $output
}
function Vm-Info($Vm) {
    $info = @{}
    foreach ($line in (Invoke-VBox @('showvminfo', $Vm.uuid, '--machinereadable'))) {
        if ($line -match '^([^=]+)="?(.*?)"?$') { $info[$matches[1]] = $matches[2] }
    }
    if ($info.UUID -ne $Vm.uuid -or (Assert-LabPath $info.CfgFile) -ne $Vm.config) { throw 'VM identity or configuration path differs from the ownership ledger.' }
    return $info
}
Assert-LabPath $lab | Out-Null
if ($Action -eq 'Prepare') {
    New-Item -ItemType Directory -Path $lab,(Join-Path $lab 'downloads'),(Join-Path $lab 'vbox-home'),(Join-Path $lab 'vms'),(Join-Path $lab 'logs'),(Join-Path $lab 'transfer') -Force | Out-Null
    if (!(Test-Path -LiteralPath $marker)) {
        $features = @(Get-CimInstance Win32_OptionalFeature | Where-Object Name -In @('Microsoft-Hyper-V-All','VirtualMachinePlatform','HypervisorPlatform') | Select-Object Name,InstallState)
        Save-Ledger @{ id='Senda-linux-lab-v1'; root=$lab; created=[DateTime]::UtcNow.ToString('o'); preexistingVirtualBox=(Test-Path -LiteralPath $compiler); installedTool=$false; windowsFeaturesBefore=$features; vms=@{} }
    }
    Write-Output $lab
    return
}
if (!(Test-Path -LiteralPath $marker)) { throw 'Prepare the lab first.' }
$ledger = Read-Ledger
$oldVBoxHome = $env:VBOX_USER_HOME
$env:VBOX_USER_HOME = Join-Path $lab 'vbox-home'
try {
    if ($Action -eq 'Download') {
        $ProgressPreference = 'SilentlyContinue'
        foreach ($key in $(if ($Asset -eq 'all') { @('virtualbox','gnome','kde') } else { @($Asset) })) {
            $item = $assets[$key]
            $destination = Assert-LabPath (Join-Path $lab ('downloads/' + $item.file))
            if ((Test-Path -LiteralPath $destination) -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $item.sha256) { Write-Output "Verified cached $key"; continue }
            Write-Output "Downloading $key..."
            Invoke-WebRequest $item.url -OutFile ($destination + '.partial')
            if ((Get-FileHash -LiteralPath ($destination + '.partial') -Algorithm SHA256).Hash -ne $item.sha256) { throw "SHA-256 mismatch: $key" }
            Move-Item -LiteralPath ($destination + '.partial') -Destination $destination -Force
            Write-Output "Verified $key"
        }
        return
    }
    if ($Action -eq 'InstallTool') {
        if (Test-Path -LiteralPath $compiler) { Write-Output 'Using the existing VirtualBox installation.'; return }
        $setup = Join-Path $lab ('downloads/' + $assets.virtualbox.file)
        if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne $assets.virtualbox.sha256) { throw 'Installer hash mismatch.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $setup
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Oracle') { throw 'Expected a valid Oracle installer signature.' }
        $arguments = @('--silent','--ignore-reboot','-msiparams','ADDLOCAL=VBoxApplication','VBOX_INSTALLDESKTOPSHORTCUT=0','VBOX_INSTALLQUICKLAUNCHSHORTCUT=0','VBOX_REGISTERFILEEXTENSIONS=0','VBOX_START=0')
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -notin @(0,3010) -or !(Test-Path -LiteralPath $compiler)) { throw "VirtualBox setup failed: $($process.ExitCode)" }
        $ledger.installedTool = $true; $ledger.installerExitCode = $process.ExitCode; Save-Ledger $ledger
        Write-Output "VirtualBox installed; exit code $($process.ExitCode)."
        return
    }
    if ($Action -eq 'Status') {
        foreach ($key in $ledger.vms.Keys) { $info = Vm-Info $ledger.vms[$key]; [pscustomobject]@{Desktop=$key;Uuid=$info.UUID;State=$info.VMState;Path=$info.CfgFile} }
        return
    }
    if ($Action -eq 'Create') {
        if ($ledger.vms.ContainsKey($Desktop)) { Vm-Info $ledger.vms[$Desktop] | Out-Null; Write-Output 'The VM already exists.'; return }
        $name = 'Senda-Linux-' + $Desktop
        $uuid = [guid]::NewGuid().ToString()
        $directory = Join-Path $lab "vms/$name"
        $config = Join-Path $directory "$name.vbox"
        Invoke-VBox @('createvm','--name',$name,'--uuid',$uuid,'--basefolder',(Join-Path $lab 'vms'),'--ostype','Ubuntu_64','--register') | Out-Null
        $vm = @{uuid=$uuid; config=$config; directory=$directory; name=$name}
        $ledger.vms[$Desktop] = $vm; Save-Ledger $ledger
        Invoke-VBox @('modifyvm',$uuid,'--memory','6144','--cpus','4','--vram','128','--graphicscontroller','vmsvga','--firmware','efi','--ioapic','on','--nic1','nat','--cable-connected1','off','--clipboard-mode','disabled','--drag-and-drop','disabled','--audio-enabled','on','--audio-controller','hda','--audio-in','off','--audio-out','on','--usb-ohci','off','--usb-ehci','off','--usb-xhci','off','--vrde','off','--snapshot-folder',(Join-Path $directory 'snapshots')) | Out-Null
        $disk = Join-Path $directory 'system.vdi'
        Invoke-VBox @('createmedium','disk','--filename',$disk,'--size','61440','--format','VDI','--variant','Standard') | Out-Null
        Invoke-VBox @('storagectl',$uuid,'--name','SATA','--add','sata','--controller','IntelAhci') | Out-Null
        Invoke-VBox @('storageattach',$uuid,'--storagectl','SATA','--port','0','--device','0','--type','hdd','--medium',$disk) | Out-Null
        Invoke-VBox @('storageattach',$uuid,'--storagectl','SATA','--port','1','--device','0','--type','dvddrive','--medium',(Join-Path $lab ('downloads/' + $assets[$Desktop].file))) | Out-Null
        Write-Output "Created $name ($uuid), offline, microphone off."
        return
    }
    if ($Action -in @('Remove','UninstallTool')) {
        if (!$ConfirmRemoval) { throw 'Removal requires -ConfirmRemoval.' }
        foreach ($vm in $ledger.vms.Values) { $info = Vm-Info $vm; if ($info.VMState -ne 'poweroff') { throw 'Power off all lab VMs before removal.' } }
        if ($Action -eq 'Remove') {
            foreach ($vm in @($ledger.vms.Values)) { Invoke-VBox @('unregistervm',$vm.uuid,'--delete') | Out-Null }
            $ledger.vms = @{}; Save-Ledger $ledger
            Write-Output 'Owned VMs and their disks removed. The ledger and downloads remain for audit/tool removal; the lab folder can be deleted after closing VirtualBox.'
            return
        }
        if ($ledger.vms.Count -ne 0 -or $ledger.preexistingVirtualBox -or !$ledger.installedTool) { throw 'Only a tool installed by this lab, after removing its VMs, can be uninstalled here.' }
        $standardConfig = Join-Path $env:USERPROFILE '.VirtualBox/VirtualBox.xml'
        if ((Test-Path -LiteralPath $standardConfig) -and (Get-Content -LiteralPath $standardConfig -Raw) -match '<MachineEntry') { throw 'Other VMs exist outside the lab; preserve VirtualBox.' }
        $entry = Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' | Where-Object { $_.DisplayName -match '^Oracle VirtualBox 7\.2\.22' } | Select-Object -First 1
        if (!$entry -or $entry.PSChildName -notmatch '^\{[0-9A-Fa-f-]+\}$') { throw 'The installed product identity changed; inspect before uninstalling.' }
        $process = Start-Process msiexec.exe -ArgumentList @('/x',$entry.PSChildName,'/qn','/norestart') -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -notin @(0,3010)) { throw "Uninstall failed: $($process.ExitCode)" }
        $ledger.installedTool = $false; Save-Ledger $ledger
        return
    }
    if (!$ledger.vms.ContainsKey($Desktop)) { throw 'Create this desktop VM first.' }
    $vm = $ledger.vms[$Desktop]; $info = Vm-Info $vm
    switch ($Action) {
        'Start' {
            if (@(Invoke-VBox @('list','runningvms')).Count -gt 0) { throw 'Only one lab VM may run at a time.' }
            Invoke-VBox @('modifyvm',$vm.uuid,'--cable-connected1',$(if ($Online) {'on'} else {'off'}),'--audio-in',$(if ($Microphone) {'on'} else {'off'})) | Out-Null
            Invoke-VBox @('startvm',$vm.uuid,'--type','headless')
        }
        'Stop' { Invoke-VBox @('controlvm',$vm.uuid,'acpipowerbutton'); Write-Output 'Requested a clean guest shutdown. Check Status before the next action.' }
        'Checkpoint' { if ($info.VMState -ne 'poweroff') { throw 'Power off before taking the clean checkpoint.' }; Invoke-VBox @('snapshot',$vm.uuid,'take','senda-clean') }
        'Reset' { if (!$ConfirmRemoval -or $info.VMState -ne 'poweroff') { throw 'Reset requires poweroff and -ConfirmRemoval.' }; Invoke-VBox @('snapshot',$vm.uuid,'restore','senda-clean') }
        'Attach' {
            if ($info.VMState -ne 'poweroff') { throw 'Power off before attaching an ISO.' }
            $full = Assert-LabPath $IsoPath
            if ([IO.Path]::GetExtension($full) -ne '.iso') { throw 'Transfer only a read-only ISO from the lab folder.' }
            Invoke-VBox @('storageattach',$vm.uuid,'--storagectl','SATA','--port','2','--device','0','--type','dvddrive','--medium',$full)
        }
    }
} finally { $env:VBOX_USER_HOME = $oldVBoxHome }
