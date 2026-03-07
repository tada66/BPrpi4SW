param(
  [string]$RemoteHost = "10.0.0.45",
  [string]$User = "tada66",
  [string]$Arch,                # auto-detect if omitted
  [string]$IdentityFile = "$env:USERPROFILE\.ssh\id_ed25519",  # path to private key
  [switch]$SelfContained,
  [switch]$SingleFile,
  [switch]$Run,
  [switch]$DetectArch,
  [switch]$UseMux,              # enable ControlMaster to avoid multiple handshakes
  [switch]$InstallService       # install / reinstall the systemd service after deploy
)

# Auto-detect remote arch if needed
if (-not $Arch -or $DetectArch) {
  Write-Host "Detecting remote architecture..."
  $remoteArch = (ssh -i $IdentityFile $User@$RemoteHost "uname -m" 2>$null).Trim()
  switch ($remoteArch) {
    "armv7l"   { $Arch = "linux-arm" }
    "aarch64"  { $Arch = "linux-arm64" }
    default    { throw "Unknown remote arch: $remoteArch. Specify -Arch." }
  }
  Write-Host "Remote uname -m: $remoteArch => using -Arch $Arch"
}

$scFlag = if ($SelfContained) { "true" } else { "false" }
$singleFileFlag = if ($SingleFile) { "true" } else { "false" }

Write-Host "Publish (Arch=$Arch SelfContained=$scFlag SingleFile=$singleFileFlag)"

dotnet publish -c Release -r $Arch --self-contained $scFlag `
  -p:PublishSingleFile=$singleFileFlag `
  -p:DebugType=None

if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed"; exit 1 }

$pub = Join-Path -Path "bin/Release/net10.0/$Arch/publish" -ChildPath ""
if (-not (Test-Path $pub)) { Write-Error "Publish folder not found: $pub"; exit 1 }
Write-Host "Source folder: $pub"

# Optional SSH multiplexing
$controlPath = "$env:TEMP\ssh-$($RemoteHost)-$($User).sock"
if ($UseMux) {
  Write-Host "Starting master connection (ControlMaster)..."
  ssh -i $IdentityFile -MNf -o ControlMaster=yes -o ControlPersist=300 -o ControlPath=$controlPath $User@$RemoteHost
  if ($LASTEXITCODE -ne 0) { Write-Error "Master connection failed"; exit 1 }
  $sshBase = "ssh -o ControlPath=$controlPath"
  $scpBase = "scp -o ControlPath=$controlPath"
} else {
  $sshBase = "ssh -i `"$IdentityFile`""
  $scpBase = "scp -i `"$IdentityFile`""
}

# Ensure remote folder
Invoke-Expression "$sshBase $User@${RemoteHost} 'mkdir -p ~/BPrpi4SW'"
Invoke-Expression "$sshBase $User@${RemoteHost} 'sudo systemctl stop startracker'"

# Copy contents
Invoke-Expression "$scpBase -r $pub/* $User@${RemoteHost}:/home/$User/BPrpi4SW"
if ($LASTEXITCODE -ne 0) { Write-Error "scp failed"; if ($UseMux) { ssh -O exit -o ControlPath=$controlPath $User@$RemoteHost }; exit 1 }

# Always ensure executable permissions
Invoke-Expression "$sshBase $User@${RemoteHost} 'chmod +x ~/BPrpi4SW/BPrpi4SW'"

# Copy the Python boot splash and install script
Write-Host "Copying boot scripts..."
Invoke-Expression "$scpBase tools/lcd_boot.py $User@${RemoteHost}:/home/$User/BPrpi4SW/lcd_boot.py"
Invoke-Expression "$scpBase scripts/install-service.sh $User@${RemoteHost}:/home/$User/BPrpi4SW/install-service.sh"
Invoke-Expression "$sshBase $User@${RemoteHost} 'chmod +x ~/BPrpi4SW/lcd_boot.py ~/BPrpi4SW/install-service.sh'"

if ($InstallService) {
  Write-Host "Installing systemd service (requires sudo)..."
  Invoke-Expression "$sshBase $User@${RemoteHost} 'sudo bash ~/BPrpi4SW/install-service.sh ~/BPrpi4SW'"
  if ($LASTEXITCODE -ne 0) { Write-Warning "Service install returned non-zero exit code." }
}

if ($Run) {
  Write-Host "Running remote binary..."
  Invoke-Expression "$sshBase $User@${RemoteHost} 'cd ~/BPrpi4SW; ./BPrpi4SW'"
}

if ($UseMux) {
  Write-Host "Closing master connection..."
  ssh -O exit -o ControlPath=$controlPath $User@$RemoteHost | Out-Null
}

Write-Host "Done."