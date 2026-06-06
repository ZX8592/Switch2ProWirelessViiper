# Switch2 Pro Wireless VIIPER

[中文版](README.zh-CN.md)

Switch2 Pro Wireless VIIPER is an unofficial WinUI 3 desktop app for using a
real Switch 2 Pro Controller directly over Bluetooth LE on Windows, then
presenting it to Steam, SDL, and games through VIIPER's `ns2pro` virtual USB
device.

The goal of this project is to provide an accessible, practical, and polished
controller compatibility solution through feature integration and a modern UI.

## Quick Start

1. Install [usbip-win2](https://github.com/vadimgrn/usbip-win2). This driver is
   required by VIIPER on Windows to attach the virtual USB controller.
2. Download the latest ZIP from this repository's
   [GitHub Releases](../../releases) page.
3. Extract the whole ZIP to a normal folder. Keep `Switch2ProWirelessViiper.exe`
   and the `app` folder together.
4. Open `Switch2ProWirelessViiper.exe`.
5. Follow the first-run guide: choose a language, check the environment, scan
   the controller, and confirm startup/tray settings.
6. Press the controller pairing button until the LEDs flash, then scan and
   connect from the app.

For release packages, `viiper.exe` is expected to be included under `app\`. If
you build locally, put `viiper.exe` and `VIIPER_LICENSES.txt` in
`tools\viiper\` before publishing.

## Runtime Path

```text
Switch 2 Pro Controller (BLE)
  -> Windows BLE GATT client
  -> FD2 input and motion parser
  -> VIIPER ns2pro TCP stream
  -> VIIPER virtual USB device
  -> Steam / SDL / games
```

## Features

- Direct Bluetooth LE scan and connection. No external bridge device is required.
- One-click connect/disconnect with VIIPER preload to reduce wait time.
- First-run onboarding for language, environment checks, controller scan, and
  startup behavior.
- Windows 11 style WinUI 3 interface.
- System tray support, tray connect/disconnect menu, start with Windows, and
  start hidden in tray.
- Virtual wired Switch 2 Pro Controller output through VIIPER `ns2pro`.
- Button, stick, trigger, and motion/gyro data forwarding.
- Stick endpoint calibration with persisted settings.
- Rumble feedback path from VIIPER back to the physical controller.
- Performance view for BLE rate, VIIPER submit rate, bridge latency, report
  interval, backlog, and errors.
- Automatic idle disconnect and single-instance protection.
- Low runtime system overhead.
- UI languages: Simplified Chinese, English, and Japanese.

## Credits and Inspiration

This app is built on research and implementations from several community
projects:

- [VIIPER](https://github.com/Alia5/VIIPER) provides the virtual USB/USBIP
  architecture and the `ns2pro` device path that lets Steam and SDL see a
  Switch 2 Pro style wired controller.
- `y700-switch2-pro-bridge` demonstrated the Switch 2 Pro BLE to USB bridge
  concept, helped validate the FD2 input report layout, rumble path, Steam
  mapping behavior, and expected polling characteristics.
- [joycon2-connector](https://github.com/Misaka10571/joycon2-connector) showed
  the practical direct-to-PC Bluetooth connection workflow and inspired the
  Windows BLE scanning/connection direction.

## Known Limitations

- The current Windows BLE path is effectively limited to a 15 ms connection
  interval on tested systems, so the app normally reaches about 67 Hz instead
  of 125 Hz or 133 Hz. Public WinRT BLE APIs do not reliably allow forcing the
  controller link to 7.5 ms.
- Voice features are not implemented.
- Headphone audio, speaker audio, and microphone audio are not implemented.
- Full HD Rumble 2 audio-level reproduction is not implemented. The app only
  forwards the currently understood rumble feedback path.
- Behavior can vary with Windows version, Bluetooth adapter, controller
  firmware, Steam/SDL version, and radio environment.
- Complete, official feature parity ultimately requires Nintendo and platform
  vendor support.

## Requirements

For normal users:

- Windows 11, or Windows 10 22H2 or newer.
- A Bluetooth adapter supported by Windows BLE.
- [usbip-win2](https://github.com/vadimgrn/usbip-win2) installed.
- A release package containing this app and `viiper.exe`.

For local builds:

- .NET SDK 10.
- `viiper.exe` from [VIIPER](https://github.com/Alia5/VIIPER/releases).

## Build

```powershell
dotnet build .\Switch2ProWirelessViiper.csproj -c Release
```

## Publish

```powershell
.\scripts\publish.ps1
```

The published app is written to:

```text
release\Switch2ProWirelessViiper.exe
release\app\
```

Open `release\Switch2ProWirelessViiper.exe`. The `app` folder contains the
WinUI/.NET runtime files, language resources, and `viiper.exe`; keep it beside
the launcher.

The `release`, `bin`, and `obj` folders are generated output and are ignored by
Git.

## Repository Layout

```text
Core\                         BLE, parser, rumble, and VIIPER bridge code
launcher\Launcher.cs           Small release launcher for the app subfolder
scripts\publish.ps1           Clean local publish script
tools\viiper\                 Optional local VIIPER binary source
App.xaml / App.xaml.cs        WinUI application bootstrap
MainWindow*.cs                Main UI and first-run onboarding
Switch2ProWirelessViiper.csproj
```

## Disclaimer

This is an unofficial community project. It is not affiliated with, endorsed
by, or sponsored by Nintendo, Valve, Microsoft, Alia5, or the authors of the
referenced projects. Nintendo Switch, Switch 2 Pro Controller, Steam, Windows,
VIIPER, and other names belong to their respective owners.

This software is experimental and provided without warranty. It interacts with
Bluetooth devices, virtual USB devices, and third-party drivers; use it at your
own risk. Do not use it in environments where unofficial input translation may
violate rules, terms of service, or anti-cheat expectations.

## License

Original code in this repository is released under the Apache License 2.0. See
[LICENSE](LICENSE).

Third-party components keep their own licenses:

- VIIPER is a separate project licensed by its upstream authors. When bundling
  `viiper.exe`, include the upstream license text, such as
  `VIIPER_LICENSES.txt`, in the release package.
- `y700-switch2-pro-bridge` is licensed under Apache License 2.0.
- `joycon2-connector` is licensed under the MIT License.
- Microsoft .NET, Windows App SDK, WinUI, and usbip-win2 are governed by their
  respective upstream licenses.
