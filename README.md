# Spotlight Improved

A replacement for Logi Options+ for the **Logitech Spotlight** presenter (model 1, USB receiver `046D:C53E` or
Bluetooth `046D:B503`) on Windows. The goal is a virtual laser pointer that:

- stays on the presentation screen only and never wanders onto the presenter view,
- disappears the moment the top button is released,
- offers several pointer styles (dot, ring, spotlight, magnifier).

## How it works

Out of the box the Spotlight behaves like an ordinary keyboard and mouse: holding the top button moves the real
Windows cursor. Over Logitech's HID++ protocol, software can **divert** a button (feature `0x1B04`
ReprogControlsV4) so that the device reports it to the software instead. With **raw XY** diverted as well, the
motion while the button is held also arrives over HID++. Windows never sees it, and the cursor stays where it is.
The app then draws its own pointer, limited to the presentation screen.

| Control | CID | Default on the remote |
|---|---|---|
| Top button, quick press | `0x0050` | left click |
| Top button, held | `0x00D8` | moves the cursor (can also divert raw XY) |
| Next, short / long press | `0x00D9` / `0x00DA` | Right arrow / nothing useful |
| Back, short / long press | `0x00DB` / `0x00DC` | Left arrow / nothing useful |

## Layout

```
src/Spotlight.Core      HID access (Windows), HID++ protocol, SpotlightController (keeps the top button
                        diverted across sleep/replug), PointerMotion (sensitivity, clamping to a screen)
src/SpotlightImproved   SpotlightImproved.exe: the tray app with the on-screen pointer
src/SpotlightProbe      spotlight-probe.exe: command-line tool to test the device
tests/                  Unit tests for the protocol layer and controller (run anywhere: dotnet test)
```

## The app

`SpotlightImproved.exe` runs in the notification area.

- **Hold the top button**: the pointer appears on the presentation screen. Release it and the pointer disappears.
  The Windows cursor does not move.
- **Pointer styles**: laser dot (with glow), ring, or spotlight (dims the rest of the screen). Color, size,
  opacity, glow, spotlight radius and dimming are all adjustable.
- **Presentation screen**: auto-detected when the pointer appears. The order is:
  1. the screen showing a PowerPoint slide show,
  2. the screen where the foreground window is fullscreen,
  3. the secondary screen (usually the projector).

  You can also pick a fixed screen from the tray menu. The pointer is drawn in a click-through window that covers
  exactly that screen, so it can't appear on the presenter view.
- **Movement**: sensitivity and acceleration are adjustable, and motion scales with screen resolution. The
  pointer can start at the center or at its last position.
- **Buttons**:
  - A quick press of the top button does nothing by default. On the remote alone it's a left click, which
    advances the slide.
  - A long press of Next/Back changes the slide, the same as a short press. On the remote alone it does nothing.
  - Short presses of Next/Back are not diverted, so they keep sending Right/Left arrow natively.
  - Both changes can be switched back to the remote's default under **Settings > Buttons**.
- **Tray menu**: style, screen, preview, settings, start with Windows.
- **Settings file**: `%APPDATA%\SpotlightImproved\settings.json`.

The app gives the button back to the device when it exits.

## Build

Needs the .NET 10 SDK. The build works on macOS, Linux or Windows; the tool itself runs on Windows only.

```sh
dotnet test
dotnet publish src/SpotlightImproved -c Release -r win-x64 -o publish/win-x64
dotnet publish src/SpotlightProbe -c Release -r win-x64 -o publish/win-x64
```

The results are `publish/win-x64/SpotlightImproved.exe` and `spotlight-probe.exe`. Both are self-contained
files, so no .NET install is needed on the target PC.

## Test on Windows

1. **Quit Logi Options+**, including `logioptionsplus_agent.exe` in Task Manager. It changes the same device
   settings.
2. Plug in the Spotlight receiver. Press a button on the Spotlight to wake it.
3. Run these in a terminal:

```
spotlight-probe list        # Logitech HID collections; HID++ ones are marked with *
spotlight-probe info        # protocol, name, firmware, battery, feature table
spotlight-probe controls    # every button the device exposes and what it supports
spotlight-probe monitor     # the main test: see below
spotlight-probe vibrate
```

`monitor` diverts the top button with raw XY. Hold the top button and move the Spotlight. You should see:

- `PRESS 0x00D8` when you press,
- `motion dx=... dy=...` lines while you move it,
- `RELEASE ...` followed by **"Windows cursor did not move"**.

Things to try while `monitor` runs:

- Let the Spotlight sleep (leave it still for about a minute), then press the top button again. Does it still
  arrive as `PRESS`, or did the cursor move? Look for "Receiver: Spotlight connected" lines.
- `spotlight-probe monitor --cid all`: divert every divertable control and see what Next and Back report.
- Add `--raw` to any command to see every HID++ report.

If a button stops working after a crash, run `spotlight-probe restore`.
