<#
  GKR Studio release: builds Windows + macOS (arm64, x64) packages and a signed update feed.

    powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 1.2.0 -Notes "Yenilikler..."

  Output: dist\release\  →  GKRStudio-win-x64.zip, GKRStudio-macos-arm64.zip, GKRStudio-macos-x64.zip,
                            update.json, update.json.sig   (upload all five to the update address)
  Signing key: %USERPROFILE%\.gkrstudio-release\private.pem (never commit / share it).
#>
param(
    [string]$Version,
    [string]$Notes = "",
    [string]$Key = "$env:USERPROFILE\.gkrstudio-release\private.pem",
    [switch]$SkipMac,
    [switch]$SkipWindows
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = 'dotnet'
if (Test-Path 'C:\Program Files\dotnet\dotnet.exe') { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }

$winProj = 'GKRStudio\GKRStudio.csproj'
$macProj = 'GKRStudio.Mac\GKRStudio.Mac.csproj'
if ($Version) {
    foreach ($p in $winProj, $macProj) {
        (Get-Content $p -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" | Set-Content $p -NoNewline
    }
} else {
    $Version = ([xml](Get-Content $winProj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not (Test-Path $Key)) { throw "Signing key not found: $Key" }
Write-Host "== GKR Studio $Version ==" -ForegroundColor Cyan

$out = Join-Path $root 'dist\release'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
$specs = @()

# ---------------- Windows ----------------
if (-not $SkipWindows) {
    # Build in %TEMP% (OneDrive locks files in place); the bundled tools are kept in dist\GKRStudio\tools.
    $toolsDir = Join-Path $root 'dist\GKRStudio\tools'
    $win = Join-Path $env:TEMP 'gkrstudio-build\win\GKRStudio'
    if (Test-Path $win) { Remove-Item $win -Recurse -Force }
    & $dotnet publish $winProj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $win -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
    if (Test-Path $toolsDir) { Copy-Item $toolsDir (Join-Path $win 'tools') -Recurse } else { Write-Warning "no tools folder at $toolsDir" }
    Get-ChildItem $win -Filter *.pdb | Remove-Item -Force
    @"
GKR Studio $Version for Windows 10/11 (64-bit)
=============================================

INSTALL
1. Extract the ZIP to a permanent folder (for example Documents\GKR Studio).
   Moving it later is fine: GKR Studio updates its own paths.
2. Run GKRStudio.exe. If Windows shows "Windows protected your PC",
   click "More info" and then "Run anyway".
3. On first start, answer "Yes". This adds the right-click menu, starts GKR Studio
   with Windows and runs it in the background (GKR icon in the system tray).
4. ffmpeg, yt-dlp and deno are already included in the "tools" folder.
   Keep that folder next to GKRStudio.exe.

USE
- Shift + right-click in Explorer: Convert wheel
- Ctrl + right-click: Tools wheel
- Alt + right-click: Actions wheel
- Keep the key held while the wheel is open and left-click a slice to choose it.
- Plain right-click > GKR Studio opens the all-in-one wheel
  (on Windows 11 it is under "Show more options").
- Ctrl + Alt + L: download the link on the clipboard (video / MP3).
- Tap Ctrl, Alt or Shift three times quickly: shortcut list.
- Language, theme and colours: GKR Studio Settings.

UPDATES
GKR Studio checks for updates automatically and installs them after asking you.
If YouTube downloads stop working before an update arrives, run "yt-dlp.exe -U"
inside the tools folder.

UNINSTALL
In Settings click "Remove from right-click menu", untick "Start in the background
with Windows", choose Exit from the tray icon, then delete the folder.
"@ | Set-Content (Join-Path $win 'README.txt') -Encoding UTF8
    $zip = Join-Path $out 'GKRStudio-win-x64.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($win, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $specs += "windows-x64=$zip"
    Write-Host "Windows: $zip"
}

# ---------------- macOS ----------------
# Bundled command-line tools for macOS (cached in dist\cache\mac-tools\<arch>): ffmpeg/ffprobe (signed, notarized
# static builds by Martin Riedl, release channel, SHA-256 checked), yt-dlp (official universal build), deno (official).
function Get-MacTools([string]$arch) {
    $cache = Join-Path $root "dist\cache\mac-tools\$arch"
    New-Item -ItemType Directory -Force $cache | Out-Null
    $need = @('ffmpeg', 'ffprobe', 'yt-dlp', 'deno') | Where-Object { -not (Test-Path (Join-Path $cache $_)) }
    if ($need.Count -eq 0) { return $cache }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $wc = New-Object Net.WebClient; $wc.Headers['User-Agent'] = 'GKR Studio release'
    $riedl = if ($arch -eq 'arm64') { 'arm64' } else { 'amd64' }
    $page = $wc.DownloadString('https://ffmpeg.martin-riedl.de/')
    foreach ($t in 'ffmpeg', 'ffprobe') {
        if (Test-Path (Join-Path $cache $t)) { continue }
        $m = [regex]::Matches($page, "/download/macos/$riedl/(\d+)_(\d+(?:\.\d+)+)/$t\.zip") | Sort-Object { [long]$_.Groups[1].Value } | Select-Object -Last 1
        if (-not $m) { throw "no macOS $riedl release build of $t found" }
        $url = 'https://ffmpeg.martin-riedl.de' + $m.Value
        $zip = Join-Path $env:TEMP "gkr-$t-$riedl.zip"
        Write-Host "  $t $($m.Groups[2].Value) ($riedl)"
        $wc.DownloadFile($url, $zip)
        $expected = ($wc.DownloadString("$url.sha256").Trim() -split '\s+')[0].ToLower()
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
        if ($expected -ne $actual) { throw "$t checksum mismatch" }
        $x = Join-Path $env:TEMP "gkr-$t-$riedl"; if (Test-Path $x) { Remove-Item $x -Recurse -Force }
        Expand-Archive $zip $x -Force
        Copy-Item (Get-ChildItem $x -Recurse -File | Where-Object Name -eq $t | Select-Object -First 1).FullName (Join-Path $cache $t)
        Remove-Item $zip, $x -Recurse -Force
    }
    if (-not (Test-Path (Join-Path $cache 'yt-dlp'))) {
        Write-Host '  yt-dlp (universal)'
        $wc.DownloadFile('https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos', (Join-Path $cache 'yt-dlp'))
    }
    if (-not (Test-Path (Join-Path $cache 'deno'))) {
        $d = if ($arch -eq 'arm64') { 'aarch64' } else { 'x86_64' }
        Write-Host "  deno ($d)"
        $zip = Join-Path $env:TEMP "gkr-deno-$d.zip"
        $wc.DownloadFile("https://github.com/denoland/deno/releases/latest/download/deno-$d-apple-darwin.zip", $zip)
        $x = Join-Path $env:TEMP "gkr-deno-$d"; if (Test-Path $x) { Remove-Item $x -Recurse -Force }
        Expand-Archive $zip $x -Force
        Copy-Item (Join-Path $x 'deno') (Join-Path $cache 'deno')
        Remove-Item $zip, $x -Recurse -Force
    }
    return $cache
}

if (-not $SkipMac) {
    $icns = Join-Path $env:TEMP 'GKRStudio.icns'
    Add-Type -AssemblyName System.Drawing
    $logo = [Drawing.Image]::FromFile((Join-Path $root 'GKRStudio\Assets\logo.png'))
    $entries = @()
    foreach ($pair in @(@('icp4', 16), @('icp5', 32), @('icp6', 64), @('ic07', 128), @('ic08', 256), @('ic09', 512), @('ic10', 1024), @('ic11', 32), @('ic12', 64), @('ic13', 256), @('ic14', 512))) {
        $bmp = New-Object Drawing.Bitmap $pair[1], $pair[1]
        $g = [Drawing.Graphics]::FromImage($bmp); $g.InterpolationMode = 'HighQualityBicubic'; $pad = [int]($pair[1] * 0.09); $g.DrawImage($logo, $pad, $pad, $pair[1] - 2 * $pad, $pair[1] - 2 * $pad)  # macOS icon grid: tile with a margin; $g.Dispose()
        $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        $entries += , @($pair[0], $ms.ToArray())
    }
    $logo.Dispose()
    $fs = [IO.File]::Create($icns); $bw = New-Object IO.BinaryWriter $fs
    # 4-byte big-endian lengths; the file length includes the 8-byte header, each entry's includes its own 8 bytes.
    # (Must be a typed byte[]: a PowerShell function returning bytes gives object[] and BinaryWriter wrote 1 byte.)
    $writeBE = { param([int]$v) [byte[]]$b = [BitConverter]::GetBytes($v); [Array]::Reverse($b); $bw.Write($b, 0, 4) }
    $total = 8; foreach ($e in $entries) { $total += 8 + $e[1].Length }
    $bw.Write([Text.Encoding]::ASCII.GetBytes('icns'), 0, 4); & $writeBE $total
    foreach ($e in $entries) { $bw.Write([Text.Encoding]::ASCII.GetBytes($e[0]), 0, 4); & $writeBE (8 + $e[1].Length); $bw.Write([byte[]]$e[1], 0, $e[1].Length) }
    $bw.Close()

    foreach ($rid in 'osx-arm64', 'osx-x64') {
        $arch = $rid.Replace('osx-', '')
        $pub = Join-Path $env:TEMP "gkrstudio-build\macpub\$rid"   # outside OneDrive (it locks files)
        if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
        & $dotnet publish $macProj -c Release -r $rid --self-contained true -p:UseAppHost=true -o $pub -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "macOS publish failed ($rid)" }
        $stage = Join-Path $env:TEMP "gkrstudio-build\macapp\$rid"
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        $app = Join-Path $stage 'GKR Studio.app\Contents'
        New-Item -ItemType Directory -Force "$app\MacOS", "$app\Resources" | Out-Null
        Copy-Item "$pub\*" "$app\MacOS" -Recurse
        Get-ChildItem "$app\MacOS" -Filter *.pdb | Remove-Item -Force
        Copy-Item $icns "$app\Resources\GKRStudio.icns"
        # ffmpeg, ffprobe, yt-dlp, deno inside Contents/MacOS/tools (signed with the app; executable bit set by zip-mac)
        $macTools = Get-MacTools $arch
        New-Item -ItemType Directory -Force "$app\MacOS\tools" | Out-Null
        Copy-Item "$macTools\*" "$app\MacOS\tools"
        New-Item -ItemType Directory -Force "$app\Resources\licenses" | Out-Null
        if (Test-Path "$toolsDir\FFmpeg-LICENSE.txt") { Copy-Item "$toolsDir\FFmpeg-LICENSE.txt" "$app\Resources\licenses" }
        [IO.File]::WriteAllText("$app\Resources\licenses\THIRD-PARTY.txt", ("Third-party tools in Contents/MacOS/tools (run unmodified, as separate programs)`n`n" +
            "ffmpeg, ffprobe: FFmpeg, GNU GPL v3 (see FFmpeg-LICENSE.txt). Source: https://ffmpeg.org  Build: https://ffmpeg.martin-riedl.de`n" +
            "yt-dlp: The Unlicense. Source: https://github.com/yt-dlp/yt-dlp`n" +
            "deno: MIT. Source: https://github.com/denoland/deno`n"), $utf8)
        # signing / Dock / login-agent helper used by both installers and the in-app updater (LF line endings)
        [IO.File]::WriteAllText("$app\Resources\finish-install.sh", ([IO.File]::ReadAllText((Join-Path $root 'tools\mac\finish-install.sh')) -replace "`r`n", "`n"), $utf8)
        @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>GKR Studio</string>
  <key>CFBundleDisplayName</key><string>GKR Studio</string>
  <key>CFBundleIdentifier</key><string>com.gkrstudio.app</string>
  <key>CFBundleExecutable</key><string>GKRStudio</string>
  <key>CFBundleIconFile</key><string>GKRStudio</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$Version</string>
  <key>CFBundleShortVersionString</key><string>$Version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSAppleEventsUsageDescription</key><string>GKR Studio reads the files selected in Finder to show the right actions.</string>
</dict></plist>
"@ | ForEach-Object { [IO.File]::WriteAllText("$app\Info.plist", $_, $utf8) }   # no BOM: macOS rejects a plist that starts with one
        # first-run helper: remove quarantine, ad-hoc sign, move to /Applications, open
        @'
#!/bin/bash
# Installs GKR Studio into /Applications and signs it ad-hoc so macOS (Apple Silicon) will run it.
cd "$(dirname "$0")"
echo "Installing GKR Studio..."
APP="/Applications/GKR Studio.app"
mkdir -p "$HOME/Library/Logs"
# sign here first: macOS may forbid changing a bundle that is already in /Applications
xattr -cr "GKR Studio.app" 2>/dev/null
/bin/bash "GKR Studio.app/Contents/Resources/finish-install.sh" "$PWD/GKR Studio.app" --sign-only 2>&1 | tee -a "$HOME/Library/Logs/GKRStudio-update.log"
pkill -x GKRStudio 2>/dev/null
rm -rf "$APP"
cp -R "GKR Studio.app" /Applications/ || { echo "Could not copy to /Applications"; read -n1 -p "Press a key..."; exit 1; }
/bin/bash "$APP/Contents/Resources/finish-install.sh" "$APP" --open 2>&1 | tee -a "$HOME/Library/Logs/GKRStudio-update.log"
echo "Done."
'@.Replace("`r`n", "`n") | Set-Content (Join-Path $stage 'Install GKR Studio.command') -NoNewline -Encoding ascii
        @"
GKR Studio $Version for macOS ($arch)
====================================

INSTALL
1. Right-click "Install GKR Studio.command" and choose Open, then Open again.
   It copies GKR Studio to your Applications folder and signs it so macOS will run it.
   (Alternative, in Terminal:  xattr -cr "GKR Studio.app" && codesign --force --deep -s - "GKR Studio.app")
2. On first start, answer "Yes". This sets up the Finder Quick Actions menu,
   starts GKR Studio at login and puts it in the menu bar.
3. Open System Settings > Privacy & Security > Accessibility and turn on GKR Studio.
   This is needed for the Shift / Control / Option + right-click wheels and the shortcuts.
4. ffmpeg, yt-dlp and deno are included. Only OCR needs Tesseract: GKR Studio Settings >
   "Install with Homebrew" (install Homebrew first from https://brew.sh)

USE
- Shift + right-click in Finder: Convert wheel
- Control + right-click: Tools wheel
- Option + right-click: Actions wheel
- Keep the key held while the wheel is open and left-click a slice to choose it.
- Finder > right-click > Quick Actions > GKR Studio also opens the wheels.
- Control + Option + L: download the link on the clipboard (video / MP3).
- Tap Control, Option or Shift three times quickly: shortcut list.
- Language, theme and colours: GKR Studio Settings.

UPDATES
GKR Studio checks for updates automatically and installs them after asking you.

UNINSTALL
In Settings click "Remove from Finder", untick "Start in the background at login",
choose Exit from the menu bar icon, then delete GKR Studio from Applications.
"@ | ForEach-Object { [IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $_, $utf8) }
        $zip = Join-Path $out "GKRStudio-macos-$arch.zip"
        & $dotnet run --project tools\ReleaseTool -c Release -- zip-mac $stage $zip
        if ($LASTEXITCODE -ne 0) { throw "zip failed ($rid)" }
        $specs += "macos-$arch=$zip"
        Write-Host "macOS ($arch): $zip"
    }
}

# ---------------- one-line macOS installer (curl downloads are not quarantined) ----------------
@'
#!/bin/bash
# GKR Studio one-line installer for macOS:
#   curl -fsSL https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/install-mac.sh | bash
set -e
if [ "$(uname -m)" = "arm64" ]; then PKG=GKRStudio-macos-arm64.zip; else PKG=GKRStudio-macos-x64.zip; fi
URL="https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/$PKG"
TMP="$(mktemp -d)"
echo "Downloading $PKG ..."
curl -fL --progress-bar "$URL" -o "$TMP/GKRStudio.zip"
ditto -x -k "$TMP/GKRStudio.zip" "$TMP/x"
mkdir -p "$HOME/Library/Logs"
# sign before moving: macOS "App Management" may forbid changing a bundle that is already in /Applications
/bin/bash "$TMP/x/GKR Studio.app/Contents/Resources/finish-install.sh" "$TMP/x/GKR Studio.app" --sign-only 2>&1 | tee -a "$HOME/Library/Logs/GKRStudio-update.log"
APP="/Applications/GKR Studio.app"
pkill -x GKRStudio 2>/dev/null || true
rm -rf "$APP"
mv "$TMP/x/GKR Studio.app" /Applications/
rm -rf "$TMP"
mkdir -p "$HOME/Library/Logs"
/bin/bash "$APP/Contents/Resources/finish-install.sh" "$APP" --open 2>&1 | tee -a "$HOME/Library/Logs/GKRStudio-update.log"
echo "GKR Studio is installed in Applications."
'@.Replace("`r`n", "`n") | ForEach-Object { [IO.File]::WriteAllText((Join-Path $out 'install-mac.sh'), $_, $utf8) }

# ---------------- one-line Windows installer (irm ... | iex) ----------------
Copy-Item (Join-Path $root 'tools\install-windows.ps1') (Join-Path $out 'install-windows.ps1') -Force

# ---------------- signed update feed ----------------
& $dotnet run --project tools\ReleaseTool -c Release -- publish $Version $Notes $Key $out @specs
if ($LASTEXITCODE -ne 0) { throw 'manifest failed' }
Write-Host "`nUpload everything in $out to the update address." -ForegroundColor Green
