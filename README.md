<p align="center"><img src="screenshots/logo.png" alt="GKR Studio logo" width="160"></p>

# GKR Studio

> Versions before 1.2.0 cannot update themselves to this release: install GKR Studio once with the links below (settings carry over).

Convert, edit and download files straight from a radial wheel — on **Windows** and **macOS**.

<p align="center">
  <img src="screenshots/wheel.png" alt="GKR Studio wheel for an image: Convert, Tools and Actions" width="380">
  <img src="screenshots/wheel-convert.png" alt="Convert wheel for an image: PNG, JPG, WEBP, TIFF, AVIF, BMP, GIF, ICO, PDF" width="380">
</p>
<p align="center">
  <img src="screenshots/wheel-tools.png" alt="Tools wheel for an image: compress, metadata, edit photo, mark up, add background, crop, redact, read QR, OCR" width="380">
  <img src="screenshots/wheel-actions.png" alt="Actions wheel: share, float shelf, copy, move, copy path, terminal, ZIP" width="380">
</p>

## Download

| Platform | Package |
|---|---|
| Windows 10/11 (64-bit) | [GKRStudio-win-x64.zip](https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/GKRStudio-win-x64.zip) |
| Mac with Apple silicon (M-series, A-series) | [GKRStudio-macos-arm64.zip](https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/GKRStudio-macos-arm64.zip) |
| Mac with Intel processor | [GKRStudio-macos-x64.zip](https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/GKRStudio-macos-x64.zip) |

### macOS: one-line install (recommended)

Open **Terminal** and paste:

```bash
curl -fsSL https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/install-mac.sh | bash
```

It picks the right package for your Mac, installs GKR Studio into Applications and starts it.
Then enable GKR Studio in **System Settings → Privacy & Security → Accessibility**.

### Windows: one-line install (recommended)

Open **PowerShell** and paste:

```powershell
irm https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/install-windows.ps1 | iex
```

It installs GKR Studio in your user folder (no admin rights needed), adds it to the Start menu and starts it. Answer **Yes** on first start.

Or download the ZIP from the table above, extract it and run `GKRStudio.exe` (if SmartScreen appears: **More info → Run anyway**).

## Features

- **Shift / Ctrl / Alt + right-click** (Mac: Shift / ⌘ Command / ⌥ Option): Convert · Tools · Actions wheels
- Images, video, audio, PDF, documents, spreadsheets, presentations, EPUB, subtitles and archives
- Download video / MP3 from YouTube and many other sites (optionally with your browser's cookies), QR code reading
- **PDF → Word that keeps the layout**: pictures, backgrounds and tables stay in place; the text stays editable with its font, size and colour
- **OCR** for scanned PDFs: a searchable, selectable text layer, only the pages you choose, with progress and time left; **Improve text** redraws blurry scanned text sharply (Windows)
- **Offline translation** of documents, e-books, subtitles and PDFs (Turkish, English, Dutch, French, German, Spanish, Arabic); language packs are downloaded only when you choose them
- English, Turkish, Dutch and French
- Signed automatic updates

FFmpeg (GPL), yt-dlp and Deno are bundled in the Windows and macOS packages, so nothing else needs to be installed for downloads and media. Licenses and source links: `tools/LISANSLAR.txt` (Windows), `GKR Studio.app/Contents/Resources/licenses` (macOS).

## What's new

| Version | Changes |
|---|---|
| 1.3.4 | macOS: ffmpeg, yt-dlp and Deno are included; video downloads work without Homebrew |
| 1.3.3 | macOS: fixed a crash when choosing a wheel slice while still holding ⌘ (e.g. Convert → Word) |
| 1.3.2 | PDF → Word: PDFs whose text can't be separated come over as pictures instead of empty pages |
| 1.3.1 | PDF → Word keeps the layout (pictures, backgrounds, positions); OCR text layer is invisible again on Windows — run OCR again on files made with an older version |
| 1.3.0 | Offline translation with downloadable language packs |
| 1.2.3 – 1.2.9 | Faster OCR with progress and time left, page choice, smaller OCR PDFs, Improve text, reliable in-app updates on Windows |

All versions: [Releases](https://github.com/gokerbekir66/gkrstudio-releases/releases).

## Lion-bite animation 🦁

With **Settings → Lion-bite animation when choosing** turned on, the lion bites the slice you pick.

<p align="center">
  <img src="screenshots/bite.gif" alt="Lion-bite animation: the lion bites the chosen PNG slice" width="440">
</p>

## Settings

Right-click menu, background mode and key + right-click wheels, language (English, Turkish, Dutch, French), theme and accent colour, signed updates, external tools, downloads from a copied link and file conversion.

<p align="center">
  <img src="screenshots/settings-full.png" alt="Full GKR Studio settings window" width="620">
</p>
