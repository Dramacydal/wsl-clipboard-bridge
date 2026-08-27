# wsl-clipboard-bridge

Fixes clipboard paste (`Ctrl+V`) of images and files inside WSL/WSLg GUI
apps (e.g. Claude Desktop).

## Why

WSLg forwards Windows clipboard images to Linux, but re-encodes them as a
`BI_BITFIELDS` BMP variant that only native Wayland clients see, and that
several common image decoders (libvips/sharp and friends) can't parse.
XWayland clients — which is how most Electron apps, including Claude
Desktop by default, render on Linux — never see an image target on the
clipboard at all. See the WSLg upstream issues:

- https://github.com/microsoft/wslg/issues/833
- https://github.com/microsoft/wslg/issues/721
- https://github.com/anthropics/claude-code/issues/50552

This tool sidesteps WSLg's own clipboard translation entirely: a small
Windows watcher grabs the clipboard content the moment it changes and
shells out to `wsl.exe` to set it on the Linux side directly. No daemon,
no network — one `wsl.exe` process per copy.

Two kinds of content are handled:

- **Raw image data** (screenshots, "Copy image" in a browser — anything
  that isn't a file) — decoded via .NET's `Clipboard.GetImage()`, which
  handles the `BI_BITFIELDS` DIB variant itself, re-encoded as a normal
  PNG, and set as `image/png` on the Linux clipboard. There's no
  original file to reference here, so this is the one case that
  actually decodes/re-encodes.
- **Files** (`Ctrl+C` on one or more files in Explorer — image files
  included) — no decoding, no re-encoding, ever. The Linux clipboard
  gets a `text/uri-list` entry: one `file:///mnt/c/...` URI per file,
  pointing at each file's own WSL-mounted path unchanged. This is the
  freedesktop-standard MIME type for "here are one or more files",
  understood by GTK file managers and by Claude Desktop's paste
  handler.

## Architecture

```
Windows                                    WSL2
--------                                   ----
clipboard change
   -> WM_CLIPBOARDUPDATE
   -> image data: Clipboard.GetImage(), re-encode as PNG
      file (Ctrl+C in Explorer): build file:///mnt/... URI
   -> write to %TEMP%\wsl-clip-bridge-<guid>.{png,uri-list}
   -> wsl.exe -e env DISPLAY=:0 WAYLAND_DISPLAY=wayland-0 \
        XDG_RUNTIME_DIR=/mnt/wslg/runtime-dir bash -c '...' \
        /mnt/c/.../wsl-clip-bridge-<guid>.{png,uri-list}
                                     -> setsid wl-copy --type <mime> <"$0" & disown
                                        sleep 0.2
                                     -> setsid xclip -selection clipboard \
                                          -t <mime> -i "$0" & disown
                                        sleep 0.3, re-assert once if lost
```

(`<mime>` is `image/png` or `text/uri-list`; the wl-copy/xclip order
shown is X11 Mode — see below.)

This sequencing exists because of several WSLg behaviors:

- **WSLg only exports `DISPLAY`/`WAYLAND_DISPLAY`/`XDG_RUNTIME_DIR` into
  interactive shell sessions** (sourced from a profile script) — a plain
  `wsl.exe -e` invocation does not inherit them, and neither does a
  systemd unit. The paths are fixed regardless of session, so they're
  hardcoded in `WslInvoker.cs`.
- **`wsl.exe -e` kills the whole session when the top-level command
  exits** — including any process xclip/wl-copy forked internally to
  stay alive and answer future paste requests (X11/Wayland selections
  have no owner-independent storage). Each is launched via
  `setsid ... & disown` to escape that teardown; without it, both
  processes vanish the instant `wsl.exe` returns even though the
  command itself reported success.
- **xclip and wl-copy can't both hold the clipboard at once here.**
  WSLg runs an XWayland<->Wayland clipboard proxy that reacts to either
  side changing by seizing ownership on the other side too, but it only
  seizes — it doesn't actually carry the bytes across. Whichever of the
  two you start most recently ends up as the sole owner with real
  content; the other is left owned-but-empty. So they run sequentially,
  never backgrounded together, and whichever protocol the tray's Mode
  setting targets goes last so it's the one left holding the real
  content.
- **WSLg's own Windows->Linux clipboard sync reacts to the same copy
  event and can clobber the image moments after ours lands**, with its
  own broken `image/bmp` translation. The script checks the target
  side's own clipboard state afterward and re-asserts once if it lost —
  the overwrite fires once per copy, not repeatedly, so a single
  re-assert is sufficient.

## Build & run

Requires the .NET SDK (built against .NET 10). The whole repo is the
project root — `WslClipboardBridge.csproj` sits at the top level.

```powershell
dotnet build -c Release
dotnet bin\Release\net10.0-windows\WslClipboardBridge.dll
```

Runs as a tray-only app (icon in the notification area, no window).
Right click for the tray menu:

- **status line** — result of the last clipboard event
- **Distro** — read-only, shows which WSL distro is targeted (the
  default one; pass `--distro <name>` on the command line for a
  multi-distro machine)
- **Enabled** — checkbox, on by default; unchecking it makes the tool
  ignore all clipboard events until re-checked
- **Mode** — submenu, **X11** (default) or **Wayland**. Controls which
  of xclip/wl-copy runs last and ends up holding the real content (see
  Architecture above). Match this to how the target app actually
  renders — XWayland by default for Claude Desktop and most Electron
  apps, Wayland only if launched with `CLAUDE_USE_WAYLAND=1` or
  equivalent. The selection persists across restarts
  (`%LOCALAPPDATA%\WslClipboardBridge\mode.txt`).

To start it automatically at login, add a shortcut to
`shell:startup` pointing at the built `.exe` — this repo does not
register that for you.

## Linux side (inside WSL) — one-time setup

Just the clipboard tools, no daemon or service to install:

```bash
sudo apt update && sudo apt install -y xclip wl-clipboard
```

## Testing

1. Build and run the Windows watcher (see above).
2. Copy a screenshot on Windows (`Win+Shift+S`), or `Ctrl+C` one or more
   files in Explorer (any type, images included).
3. On the Linux side: `xclip -selection clipboard -t TARGETS -o` should
   list `image/png` (screenshot / raw clipboard image data) or
   `text/uri-list` (anything copied as a file, images included).
4. Paste (`Ctrl+V`) into Claude Desktop (or any GUI app) inside WSL.

Debug log (every clipboard event, the `wsl.exe` exit code, stdout/stderr)
is written to `%TEMP%\wsl-clip-bridge.log`.

## CI

`.github/workflows/release.yml` builds a framework-dependent
single-file `win-x64` publish (under 1 MB — not bundling the .NET
runtime) and attaches it to a GitHub Release whenever a tag matching
`v*` is pushed. Running the published exe requires the [.NET 10 Desktop
Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) already
installed on the target machine.

Repo setting required: **Settings → Actions → General → Workflow
permissions** must be **"Read and write permissions"** — the default
"Read repository contents and packages permissions" caps what a
workflow's `permissions:` block can request, and creating a release
needs `contents: write`. A workflow can only narrow the repo-wide
default, never widen it.

## Notes

- Each copy spawns one `wsl.exe` process — there's a small (roughly
  hundreds-of-ms) delay before the content lands in the Linux
  clipboard. Fine for an explicit copy action; not meant for a hot loop.
- Identical consecutive clipboard content (by hash) is not re-sent,
  since some apps fire `WM_CLIPBOARDUPDATE` more than once per copy
  while adding clipboard formats incrementally.
- `Ctrl+C` on multiple files at once: all of them are sent, one
  `file://` URI per line in the same `text/uri-list` entry. Whether a
  given Linux app treats that as multiple attachments is up to the
  app — Claude Desktop's compose UI has shown visual duplication (extra
  thumbnails) with 3+ files at once, though the files themselves attach
  correctly.
