"""Rebuild Winget UI and executable icons from the checked-in, user-provided artwork."""

from pathlib import Path
from PIL import Image

REPO = Path(__file__).resolve().parent.parent
ASSETS = REPO / "src/modules/Winget/Assets"
SOURCE = ASSETS / "Winget.source.png"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def main():
    with Image.open(SOURCE) as opened:
        source = opened.convert("RGBA")
    if source.width != source.height:
        raise ValueError("Winget source artwork must remain square; do not crop it.")

    large = source.resize((256, 256), Image.Resampling.LANCZOS)
    large.save(ASSETS / "Winget.png", optimize=True)
    frames = [source.resize((size, size), Image.Resampling.LANCZOS) for size in ICO_SIZES]
    large.save(ASSETS / "Winget.ico", sizes=[(size, size) for size in ICO_SIZES], append_images=frames)
    for kind, size in (("Icons", 36), ("Modules", 256)):
        target = REPO / "src/settings-ui/Settings.UI/Assets/Settings" / kind / "Winget.png"
        source.resize((size, size), Image.Resampling.LANCZOS).save(target, optimize=True)

    with Image.open(ASSETS / "Winget.ico") as icon:
        assert icon.ico.sizes() == {(size, size) for size in ICO_SIZES}
        for size in ICO_SIZES:
            frame = icon.ico.getimage((size, size)).convert("RGBA")
            assert frame.getextrema()[3] == (0, 255), "Transparency was lost."
    print("Winget assets generated: original RGBA artwork, 36/256 px PNGs and 10 ICO sizes.")


if __name__ == "__main__":
    main()
