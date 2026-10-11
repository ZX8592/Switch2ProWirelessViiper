Place VIIPER for local release builds in this folder.

The bundled and supported version is VIIPER v0.9.0 (Windows x64).
Download the official signed
[`viiper-windows-amd64.exe`](https://github.com/Alia5/VIIPER/releases/download/v0.9.0/viiper-windows-amd64.exe)
and rename it to `viiper.exe`.

SHA-256: `142c144f6abee8b8a5f6a1abbc7018683444de71d2e45102cde6aaab9b2223e6`.

Expected files:

- `viiper.exe`
- `VIIPER_LICENSES.txt`

`viiper.exe` is ignored by Git. The publish script copies these files into the
published app folder when they are present.

`VIIPER_LICENSES.txt` contains the v0.9.0 executable's embedded license notices.
The app starts VIIPER with `--update-notify=none`, disabling its update checks
and update dialogs. Independently started VIIPER processes are not changed.
