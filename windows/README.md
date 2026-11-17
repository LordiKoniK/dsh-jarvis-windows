# Jarvis panel — Windows

The Windows build of the Jarvis orb: a borderless, always-on-top particle orb with
a quick reply bar and a tray icon. It talks to the DSH host plugin over the same
`/jarvis/*` HTTP routes the macOS build uses.

## Build

```powershell
# from a normal PowerShell prompt
.\windows\build.ps1

# or explicitly
dotnet build windows\JarvisPanel\JarvisPanel.csproj
```

Both produce:

```
windows\dist\jarvis-panel.exe
```

`build.ps1` takes `-Configuration Release`; the default is `Debug`.

Requirements: the .NET 9 SDK. There are **no NuGet packages** — the panel uses the
BCL and WinForms only, so it builds offline. The exe is framework-dependent: the
target machine needs the **.NET 9 Desktop Runtime** (`Microsoft.WindowsDesktop.App`
9.x). A self-contained publish is not wired up yet.

To run it by hand:

```powershell
.\windows\dist\jarvis-panel.exe
```

There is also a headless check of the quick bar's placement maths, which is worth
running after touching `Core/Placement.cs` or `UI/ScreenMetrics.cs`:

```powershell
.\windows\dist\jarvis-panel.exe --selftest   # exit 0 = pass; report in %TEMP%\jarvis-selftest.txt
```

It checks absolute-pixel/point conversion against synthetic multi-monitor geometry,
including displays left of, right of and above the primary — the cases a
single-monitor development machine cannot exercise, and where a dropped monitor
origin silently puts the quick bar a whole monitor away from the orb.

It writes to no console (it is a `WinExe`), so diagnostics go to
`%USERPROFILE%\.dsh\jarvis\panel.log`. Settings live in
`%USERPROFILE%\.dsh\jarvis\panel.json`, the same file and key names the macOS
build uses.

## What it does

| Input | Result |
| --- | --- |
| Drag the orb | Moves it; releasing within 10pt of a screen edge snaps it flush and collapses it into a particle strip |
| Click the orb (or the strip) | Opens the quick bar; click again to close |
| Right-click the orb | Menu: open quick bar, reset position, quit |
| Hover the orb or strip | Blooms the strip back into a full orb and shows standby |
| `Ctrl+Alt+J` | Opens / focuses / closes the quick bar (change `hotKey` in `panel.json`) |
| Tray icon | Every surface: quick bar, history, target, pending cards, auto-approvals, settings, orb visibility, reset, quit |

The quick bar is the full spec §6 surface: a target picker (`Ctrl+1`…`Ctrl+9`, `Tab`
cycles), conversation history,
status line that carries the error, the Esc hint, the toast and the send target.
`Esc` twice closes it; the first press only shows the hint (`EscapeGate`).

Pending approvals and questions appear as their own column of cards beside the orb,
from the tray, or by opening the bar for a typed answer. The cards never activate —
they are `WS_EX_NOACTIVATE` tool windows, so clicking Allow does not move focus away
from whatever the user is typing in.

With no host — or before the plugin is installed — the orb turns **red** and keeps
retrying with backoff. It never exits on its own. The only ways out are the tray's
Quit, the orb's context menu, or a second instance of the single-instance guard
(which exits silently).

## Files

```
windows/
  build.ps1                 build + print the exe path
  scripts/                  verification helpers (see below)
  JarvisPanel/
    Program.cs              entry point, single instance, hotkey, message loop
    App/
      Log.cs                %USERPROFILE%\.dsh\jarvis\panel.log with rotation
      StateLoop.cs          RefreshAsync / WaitForChangeAsync with backoff
      UiActions.cs          every surface and the actions the tray and orb menus share
    UI/
      OrbWindow.cs          the orb: render, drag, snap, dock, hover, menu
      QuickBarWindow.cs     the full quick bar (target picker, history, input)
      ApprovalCardWindow.cs the pending approval / question column
      AutoApprovalsWindow.cs the auto-approval audit list with per-rule revoke
      SettingsWindow.cs     settings dialog over SettingsStore
      Widgets.cs            HUD card, pill button, stack layout, font cache
      TrayIcon.cs           NotifyIcon with a generated orb icon
      LayeredWindow.cs      per-pixel-alpha window base
      NativeMethods.cs      the Win32 surface
      ScreenMetrics.cs      pixels <-> points, per-monitor DPI
      Theme.cs              colours, fonts, HUD surfaces
    Core/                   platform-agnostic logic, ported from macOS
```


`Core/` is a direct port of `macos/Sources/JarvisPanelCore/` and is deliberately
free of WinForms references so the two builds stay in step.

## Mapping to `macos/`

| macOS | Windows |
| --- | --- |
| `JarvisPanelApp.swift` (`@main`, `AppDelegate`) | `Program.cs` (`Main`, `PanelContext`) |
| single-instance pidfile at `~/.dsh/jarvis/panel.pid` | named mutex `Local\jarvis-panel-single-instance` |
| `AppController.swift` | `OrbWindow` + `StateLoop` + `UiActions` |
| `OrbPanel.swift`, `OrbMetalView.swift` | `UI/OrbWindow.cs` (GDI+ instead of Metal) |
| `OverlayPanel.swift`, `OverlayRootView.swift` | `UI/QuickBarWindow.cs` (Stage A subset) |
| `JarvisPanelCore/*.swift` | `Core/*.cs`, file for file |
| `HotKey.swift` (Carbon) | `NativeMethods.RegisterHotKey` + `WM_HOTKEY` |
| no tray icon (AppKit menu bar) | `UI/TrayIcon.cs` |
| `build.sh` | `build.ps1` |

### Coordinate systems

`Core/` works in **absolute screen points**: a point is the device pixel divided by
the monitor's scale and nothing else, and the monitor's own origin (often negative)
is part of both numbers. `GeometryConversion` is therefore a bare multiply or
divide, and every rectangle `Placement` returns is already in that space. Adding a
monitor origin a second time looks harmless on the primary display, where the origin
is zero, and displaces everything by a whole monitor anywhere else — which is what
`--selftest` exists to catch.

`Core/` uses a **y-down** screen space, exactly as the Swift original uses a y-up
one; every ported formula flips its vertical terms. Particle sprites carry one extra
unit: `ParticleSim` speaks *orb units* (`ParticleSim.UnitPoints` = 55.2 points
each), which `OrbWindow.Render` scales to pixels.

Two GDI+ traps are documented at their call sites because both fail **silently**:

* a negative y-scale makes `FillRectangle`/`FillPath` discard every draw, so the y
  flip is done per sprite in `OrbWindow.Render` rather than with a transform;
* `Graphics.Clear` obeys the current transform, so `LayeredWindow.Redraw` resets the
  transform before clearing.

## Focus rules

The panel is a background assistant, so "which window may take focus" is a design
constraint rather than an implementation detail:

* The orb is a layered window with `WS_EX_NOACTIVATE` and never becomes the
  foreground window. The one exception is the duration of its own right-click menu,
  which Windows will not dismiss for a no-activate owner; the style is dropped and
  put straight back, and the previous foreground window is restored.
* The approval cards are also `WS_EX_NOACTIVATE` tool windows. They appear on their
  own when a request arrives, and must not eat a keystroke meant for the app the
  user is working in.
* The quick bar, the auto-approval list and the settings dialog do activate — all
  three are only opened by a deliberate user action, and all three take typing.
  The quick bar requests focus once, on the hidden → visible transition: the model
  raises a change about once a second, and re-activating on each of those would pull
  the user out of whatever they were doing.

`windows\scripts\focus-audit.ps1` prints the ex-style of every window the panel
owns and whether the orb is the foreground window, which is the quickest way to
check none of the above regressed.

## Verifying a change

The panel has no console and most of its surfaces only appear in response to host
state, so `windows\scripts\` holds the checks used while building Stage B. All of
them are read-only apart from `install-dist.ps1`, and none of them move the mouse
into the orb (synthetic input does not reach a `WS_EX_NOACTIVATE` layered window, so
they inspect windows and geometry instead):

| Script | What it proves |
| --- | --- |
| `launch-check.ps1` | Starts the exe, waits, and fails if the log gained a `frame failed` or exception line |
| `focus-audit.ps1` | Orb keeps `WS_EX_NOACTIVATE` and is not the foreground window |
| `list-panel-windows.ps1` | Every top-level window the process owns, with visibility, ex-style and rect |
| `list-bar-children.ps1` | The quick bar's live child windows and their bounds — the ground truth for the row layout |
| `window-state.ps1` | Per-window DWM cloaking, minimise state and z-order |
| `host-state.ps1` | The host snapshot the panel renders from (pending, auto-approvals, sessions) |
| `watch-quickbar.ps1` | Samples the bar's visibility after a gesture, to catch a bar that comes and goes |
| `move-bar.ps1` | Parks the bar on an empty part of the desktop for a clean screenshot |
| `install-dist.ps1` | Copies a staged build into `dist` when the supervised exe holds the file |
| `tray-invoke.ps1`, `tray-list.ps1` | Finds and invokes the tray icon through UI Automation |

Two traps found this way, both worth remembering before touching the surfaces:

* **A one-shot `Show()` plus `Activate()` on every model change** steals focus about
  once a second. The bar now requests focus only on the hidden → visible transition.
* **`Dock = Fill` silently overrides explicit bounds.** The caret and the status
  label were docked, so WinForms rewrote their `Location`/`Size` after every
  assignment and the caret covered the whole bar. Every control in the bar is placed
  at explicit offsets and none of them dock.

## Deliberately not done yet (after Stage B)

Stage B closed the window surfaces and the English string pass. These gaps remain:

* **`VisibilityPolicy`** — the orb is always fully opaque. Proximity fade, hiding in
  fullscreen, and the hidden-apps list need a foreground-window observer
  (`NativeMethods.SetWinEventHook` is already declared for it). The settings window
  says so next to those controls rather than pretending they work.
* **Multi-monitor position memory** — positions are keyed by monitor device name and
  restored for the primary monitor only; dragging to another monitor and restarting
  does not restore the orb there. Per-monitor DPI is read at runtime, but the canvas
  is not rebuilt if the orb is dragged to a monitor with a different scale.
* **Reduce motion** — `ParticleSim.ReduceMotion` is wired to the Windows animation
  accessibility flag and re-read when the settings dialog saves, but there is no
  live observer, so a change made in Windows itself only applies on the next state
  change or settings save.

## Strings

The panel is an English deployment. Every user-facing string is English, and the
strings that came from the macOS build in Chinese were translated rather than
duplicated: `PanelModel.JarvisLabel`, the send / answer / managed-set / narration /
auto-approval error prefixes in `PanelModel`, `JarvisApiException`'s messages,
`ParticleTierInfo.Label` and the caption's `name: text` separator.

Two Chinese strings deliberately remain, neither user-visible: the
`"贾维斯-"` / `"[贾维斯]"` worker-title prefixes that `SessionInfo.ShortName`
strips (the host still writes them for sessions created by the macOS build), and the
comments that document them.
