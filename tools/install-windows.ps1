# GKR Studio one-line installer for Windows. In PowerShell:
#   irm https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/install-windows.ps1 | iex
# Installs (or updates) GKR Studio in %LOCALAPPDATA%\Programs\GKR Studio, adds a Start menu shortcut and starts it.
# No admin rights needed. Files downloaded this way carry no "downloaded from the internet" mark, so SmartScreen
# does not stop the app on first start.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # much faster downloads in Windows PowerShell 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$url  = 'https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/GKRStudio-win-x64.zip'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\GKR Studio'
$tmp  = Join-Path $env:TEMP ('gkrstudio-install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host 'Downloading GKR Studio (about 250 MB)...'
    Invoke-WebRequest -Uri $url -OutFile (Join-Path $tmp 'GKRStudio.zip') -UseBasicParsing
    Write-Host 'Extracting...'
    Expand-Archive -Path (Join-Path $tmp 'GKRStudio.zip') -DestinationPath $tmp -Force

    # close a running copy (also the former "LionMedia"), then copy the new files over
    $running = @(Get-Process -Name 'GKRStudio', ('Lion' + 'Media') -ErrorAction SilentlyContinue)
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    $running | ForEach-Object { try { $_.WaitForExit(10000) | Out-Null } catch {} }   # all instances (tray + settings) must be gone
    Start-Sleep -Milliseconds 500
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $tmp 'GKRStudio\*') -Destination $dest -Recurse -Force
    Get-ChildItem -Path $dest -Recurse -File | Unblock-File

    $exe = Join-Path $dest 'GKRStudio.exe'
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'GKR Studio.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $dest
    $shortcut.Description = 'GKR Studio'
    $shortcut.Save()

    Write-Host "GKR Studio is installed in $dest (Start menu: GKR Studio). Starting..."
    Start-Process -FilePath $exe
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
