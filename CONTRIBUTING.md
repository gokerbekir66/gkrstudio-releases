# Contributing

Thanks for helping make GKR Studio better! The full source code is in this repository under the [MIT license](LICENSE).

## Report a bug

1. Update to the latest version first (Settings → Check for updates) and see if the problem is still there.
2. Search the [issues](https://github.com/gokerbekir66/gkrstudio-releases/issues) to see if it was already reported.
3. Open a new issue with the **Bug report** template and include:
   - GKR Studio version (Settings → Updates) and Windows or macOS version;
   - the file type and the wheel slice you used (e.g. PDF → Convert → DOCX);
   - what you expected and what happened, with a screenshot if possible;
   - on macOS, the last lines of `~/Library/Logs/GKRStudio.log`.

Please don't attach private or confidential files. If a file is needed to reproduce the problem, make a small sample without personal data.

## Suggest a feature

Open an issue with the **Feature request** template. Describe what you want to do and why, not only how.

## Code

- `GKRStudio/` — Windows app (WPF, .NET 8). `GKRStudio/Core/` holds the shared logic (wheel catalog, converters, downloader, updater, translations).
- `GKRStudio.Mac/` — macOS app (Avalonia 11). It links the shared files from `GKRStudio/Core/`; platform code is in `GKRStudio.Mac/Platform/`.
- `tools/` — release script, installers and the signing tool.

Build:

```
dotnet build GKRStudio/GKRStudio.csproj -c Release
dotnet build GKRStudio.Mac/GKRStudio.Mac.csproj -c Release
```

Guidelines for pull requests:

- Keep the wheel stateless: it is built only from the file type, the clipboard and the settings.
- Never overwrite the user's files: results go next to the original with a new name.
- Every user-visible text is Turkish in code and translated in `GKRStudio/Core/L.cs` (English, Dutch, French).
- One topic per pull request, and describe how you tested it (Windows, macOS or both).

Release builds are signed with a private key that is not in this repository, so only official packages can update installed copies.

## Translations

If a text is wrong, unclear or missing in your language, open an issue or a pull request for `GKRStudio/Core/L.cs`.

Please follow the [code of conduct](CODE_OF_CONDUCT.md).
