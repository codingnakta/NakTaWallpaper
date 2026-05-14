# NakTaWallpaper

A simple Windows live wallpaper app that runs a WebView2 page behind your desktop icons. Double-click an empty area of the desktop to interact with the web; double-click again (or press Esc) to return.

## Features

- WebView2-powered web wallpaper behind desktop icons
- Double-click toggle between wallpaper and interactive modes
- Desktop icons remain fully functional in wallpaper mode (single click, double-click to open, drag, etc.)
- Smart icon detection: double-clicking a desktop icon opens the file normally (does not trigger toggle)
- Keyboard shortcuts for fast control
- Works on Windows 10 and 11, including Win11 "raised desktop" mode

## Controls

| Action | Effect |
|--------|--------|
| Double-click empty desktop area | Enter interactive mode (web becomes clickable) |
| Double-click in interactive mode | Return to wallpaper mode |
| `Esc` | Return to wallpaper mode |
| `Ctrl + Shift + D` | Toggle mode (anywhere) |
| `Ctrl + Shift + Q` | Quit |

## Requirements

- Windows 10 / 11 (x64)
- Microsoft Edge WebView2 Runtime (pre-installed on Win11)
  - Download for Win10: https://developer.microsoft.com/microsoft-edge/webview2/
- .NET 10 SDK (for building only)

## Build from Source

```powershell
# Debug build
dotnet build

# Release self-contained publish (no .NET needed on target machine)
dotnet publish -c Release -r win-x64 --self-contained true -o dist\NakTaWallpaper
```

## Build the Installer

Requires Inno Setup 6 (https://jrsoftware.org/isinfo.php).

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer.iss
```

The installer is generated in `dist\`.

## How It Works

The app attaches its WebView2 host window to the desktop's wallpaper layer using the Progman / WorkerW technique:

1. Sends the `0x052C` message to Progman to spawn the WorkerW wallpaper layer
2. Detects Win11 "raised desktop" mode via the `WS_EX_NOREDIRECTIONBITMAP` flag on Progman
3. On raised desktop: parents the window to Progman with `WS_CHILD` + `WS_EX_LAYERED` (alpha 255), Z-ordered behind `SHELLDLL_DefView`
4. On classic desktop: parents directly to the empty WorkerW
5. A low-level mouse hook detects double-clicks for mode toggling
6. In interactive mode, mouse input is forwarded to the WebView2's Chrome HWND via `PostMessage`
7. `SHELLDLL_DefView` is never modified — icons keep working normally

## Credits

Desktop attachment technique inspired by [Lively Wallpaper](https://github.com/rocksdanister/lively), an open-source live wallpaper app. The implementation here is original code written from scratch.

## License

MIT License — see [LICENSE](LICENSE).
