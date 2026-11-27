# Accessibility

GKR Studio should be usable by as many people as possible. This page describes what works today, the known limits and how to report a barrier.

## What is supported

- **Keyboard on the wheel:** keys **1–9** choose a slice, **Backspace** goes back one level, **Esc** closes the wheel.
- **Shortcuts:** Ctrl+Alt+L (Windows) / ⌃⌥L (macOS) opens the download wheel; quickly tapping Ctrl, Alt or Shift three times opens the shortcuts window. Keys for the Convert, Tools and Actions wheels can be changed in Settings.
- **Readable wheel:** large labels, optional icons under each label, a light and a dark theme and eight accent colours.
- **Less motion:** the fire animation can be turned off in Settings (Fire animation when choosing).
- **Languages:** English, Turkish, Dutch and French.
- **Settings window and dialogs** use standard Windows (WPF) and macOS (Avalonia) controls, so they follow the system text size and work with the keyboard.

## Supported environments

- Windows 10 and 11 (64-bit)
- macOS 11 or later (Apple silicon and Intel)

## Known limitations

- The radial wheel is drawn as a custom graphic; screen readers (Narrator, NVDA, VoiceOver) may not read its slices. Settings, dialogs and the Explorer / Finder right-click menu entries are the accessible way to start the same actions.
- The wheel opens at the mouse position and is made for mouse or touchpad use; keyboard selection works once it is open.
- The colour of the selected slice follows the accent colour; choose one with enough contrast if you have low vision.

## Report a barrier

Open an [issue](https://github.com/gokerbekir66/gkrstudio-releases/issues) and describe what you tried, which assistive technology you use (and its version), your operating system and what went wrong. Barriers are treated as bugs.
