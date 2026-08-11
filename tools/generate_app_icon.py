from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "MuseRAM.App" / "Assets"
FONT_PATH = Path(r"C:\Windows\Fonts\segoeuib.ttf")


def centered_text(draw, center, text, font, fill):
    bounds = draw.textbbox((0, 0), text, font=font)
    width = bounds[2] - bounds[0]
    height = bounds[3] - bounds[1]
    x = center[0] - width / 2 - bounds[0]
    y = center[1] - height / 2 - bounds[1]
    draw.text((x, y), text, font=font, fill=fill)


def main():
    size = 1024
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle(
        (66, 66, 958, 958),
        radius=174,
        fill="#09090B",
        outline="#27272A",
        width=20,
    )
    draw.rounded_rectangle(
        (96, 96, 928, 928),
        radius=144,
        outline="#3F3F46",
        width=8,
    )

    font = ImageFont.truetype(str(FONT_PATH), 470)
    centered_text(draw, (512, 512), "M", font, "#FAFAFA")

    png = image.resize((512, 512), Image.Resampling.LANCZOS)
    png.save(ASSETS / "MuseRAM-icon.png", optimize=True)
    image.save(
        ASSETS / "MuseRAM.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
