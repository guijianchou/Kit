# Winget artwork

`Winget.source.png` is the unmodified 121 × 121 RGBA image supplied by the user as
`Downloads/winget.png` (SHA-256 `ac465b5c5c5381f700668926e133176394615617cc8619286bf2326c2dc8a3a2`).

`Winget.png` is the 256 px notification image. `Winget.ico` contains transparent
16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px frames and is shared by the service
executable and native module resource. Settings uses a 36 px navigation image and
a 256 px module image derived from this same source.

To regenerate them with Python and Pillow, run `python tools/Generate-WingetAssets.py`
from the repository root. The generator preserves the whole composition and alpha
channel; it never removes the original shadows or fills the transparent background.

After building, run `python tools/tests/Winget/Verify-WingetIcons.py`. The verifier
reads the icon resources from `x64/Debug/Kit.WingetService.exe` and
`Kit.WingetModuleInterface.dll` as data, compares every frame to `Winget.ico`, and
does not start the service or execute the DLL. Use `--runtime-root` for another output.
