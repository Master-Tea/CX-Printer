"""Regenerate transparent Windows shell icons from the supplied application image.

Run with Python and Pillow. Keep all three target-size forms: omitting either
unplated form makes Windows add a theme/accent-colored icon backplate.
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "CX Printer/CX Printer/Assets/AppIcon.png"
OUTPUT = ROOT / "packaging/msix/PackageAssets"
TARGET_SIZES = (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)


def main():
    with Image.open(SOURCE) as original:
        image = original.convert("RGBA")
    if image.getchannel("A").getextrema()[0] != 0:
        raise ValueError("The source application icon must have a transparent background.")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for name, size in (("Square44x44Logo", 44), ("Square150x150Logo", 150), ("StoreLogo", 50)):
        image.resize((size, size), Image.Resampling.LANCZOS).save(OUTPUT / f"{name}.png")
        for scale in (200, 400):
            pixels = size * scale // 100
            image.resize((pixels, pixels), Image.Resampling.LANCZOS).save(
                OUTPUT / f"{name}.scale-{scale}.png"
            )
    for size in TARGET_SIZES:
        icon = image.resize((size, size), Image.Resampling.LANCZOS)
        for form in ("", "_altform-unplated", "_altform-lightunplated"):
            icon.save(OUTPUT / f"Square44x44Logo.targetsize-{size}{form}.png")
    print(f"Generated {9 + len(TARGET_SIZES) * 3} transparent shell icon assets in {OUTPUT}")


if __name__ == "__main__":
    main()
