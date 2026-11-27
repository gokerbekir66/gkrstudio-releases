# Third-party software

GKR Studio's own code is under the [MIT license](LICENSE). The release packages also contain separate programs and libraries that keep their own licenses.

## Programs bundled in the packages (run as separate processes)

| Program | License | Source |
|---|---|---|
| FFmpeg / ffprobe | GPL | https://ffmpeg.org — Windows builds: https://github.com/BtbN/FFmpeg-Builds · macOS builds: https://ffmpeg.martin-riedl.de |
| yt-dlp | Unlicense | https://github.com/yt-dlp/yt-dlp |
| Deno | MIT | https://github.com/denoland/deno |

Their license texts are in `tools/LISANSLAR.txt` and `tools/FFmpeg-LICENSE.txt` (Windows) and `GKR Studio.app/Contents/Resources/licenses` (macOS).

## Libraries (NuGet)

| Library | License |
|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent (macOS app) | MIT |
| Microsoft.ML.OnnxRuntime (offline translation) | MIT |
| PDFsharp | MIT |
| PDFtoImage (macOS) | MIT |
| PdfPig | Apache-2.0 |
| SharpCompress | MIT |
| ZXing.Net | Apache-2.0 |

## Downloaded on request

- Offline translation language packs: OPUS-MT models from the Helsinki-NLP project (CC-BY 4.0), downloaded only when you choose a language pair in Settings.
- Optional on macOS: tesseract (Apache-2.0) and LibreOffice (MPL-2.0), installed by you with Homebrew.
