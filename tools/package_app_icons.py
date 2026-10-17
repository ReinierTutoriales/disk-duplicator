"""Package the unchanged master logo at Windows icon sizes (requires Pillow).

No redesign or sharpening: the original AppLogo.png remains the source of truth.
Run from any directory; WinUI selects the scale-qualified PNG resources through MRT.
"""
from pathlib import Path

from PIL import Image


def main():
    assets = Path(__file__).resolve().parents[1] / "dotnet" / "RepartoCopier.WinUI" / "Assets"
    with Image.open(assets / "AppLogo.png") as source:
        master = source.convert("RGBA")
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    if master.width < max(sizes) or master.height < max(sizes):
        raise ValueError("The master must cover all icon sizes without upscaling")
    master.save(assets / "AppLogo.ico", sizes=[(size, size) for size in sizes])
    with Image.open(assets / "AppLogo.ico") as icon:
        assert icon.ico.sizes() == {(size, size) for size in sizes}
    for logical_size in [16, 48]:
        for scale in [100, 125, 150, 200, 250, 300, 400]:
            pixels = logical_size * scale // 100
            suffix = "" if scale == 100 else f".scale-{scale}"
            target = assets / f"AppIcon{logical_size}{suffix}.png"
            master.resize((pixels, pixels), Image.Resampling.LANCZOS).save(target)
            with Image.open(target) as generated:
                assert generated.size == (pixels, pixels)
    print(f"ICO: {len(sizes)} verified sizes; PNG: 14 verified scale resources; master unchanged")


if __name__ == "__main__":
    main()
