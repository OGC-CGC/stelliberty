#!/usr/bin/env python3
"""Export app and tray resources without building or launching the application.

Run on macOS with Xcode (Icon Composer), rsvg-convert and ImageMagick installed.
The .icon document owns the app appearance; each tray uses its platform's SVGs,
with Windows sharing the Linux SVGs. Generated resources are checked in so normal
application builds do not require these conversion tools.
"""

import shutil
import subprocess
import tempfile
from pathlib import Path


ASSETS = Path(__file__).resolve().parents[1] / "src" / "Stelliberty.Desktop" / "Assets"
STATES = ("disabled", "proxy_enabled", "tun_enabled", "proxy_tun_enabled")
APP_SIZES = (16, 24, 32, 48, 64, 128, 256)
TRAY_SIZES = (16, 20, 24, 32, 40, 48, 64)
MAC_EXPORTS = (
    (16, 1), (16, 2), (32, 1), (32, 2), (128, 1),
    (128, 2), (256, 1), (256, 2), (512, 1), (1024, 1),
)


def run(*args: object) -> None:
    subprocess.run([str(arg) for arg in args], check=True)


def pad_macos_icon(path: Path, pixels: int) -> None:
    # Icon Composer exports fill the canvas. The ICNS fallback needs the previous
    # macOS layout: an 824px body centered on a 1024px transparent canvas.
    body_pixels = round(pixels * 824 / 1024)
    run("magick", path, "-resize", f"{body_pixels}x{body_pixels}",
        "-background", "none", "-gravity", "center", "-extent", f"{pixels}x{pixels}", path)


def main() -> None:
    developer_dir = Path(subprocess.check_output(
        ["xcode-select", "--print-path"], text=True).strip())
    ictool = (developer_dir.parent / "Applications" / "Icon Composer.app"
              / "Contents" / "Executables" / "ictool")
    if not ictool.is_file():
        raise FileNotFoundError(f"Icon Composer export tool does not exist: {ictool}")
    for tool in ("rsvg-convert", "magick"):
        if shutil.which(tool) is None:
            raise FileNotFoundError(f"Required icon conversion tool is missing: {tool}")

    with tempfile.TemporaryDirectory(prefix="stelliberty-icons-") as temporary:
        staging = Path(temporary)
        for platform in ("macos", "linux", "win"):
            (staging / platform / "tray").mkdir(parents=True)

        for size, scale in MAC_EXPORTS:
            output = staging / "macos" / f"app_icon-macOS-Default-{size}x{size}@{scale}x.png"
            run(ictool, ASSETS / "macos" / "app_icon.icon", "--export-image",
                "--output-file", output, "--platform", "macOS", "--rendition", "Default",
                "--width", size, "--height", size, "--scale", scale)

        app_image = staging / "macos" / "app_icon-macOS-Default-1024x1024@1x.png"
        run("magick", app_image, "-define",
            "icon:auto-resize=" + ",".join(map(str, APP_SIZES)), staging / "win" / "app_icon.ico")
        shutil.copy2(staging / "macos" / "app_icon-macOS-Default-256x256@1x.png",
                     staging / "linux" / "app_icon.png")

        # Keep Windows/Linux exports unchanged; only the macOS ICNS uses padding.
        for size, scale in MAC_EXPORTS:
            pad_macos_icon(
                staging / "macos" / f"app_icon-macOS-Default-{size}x{size}@{scale}x.png",
                size * scale)

        for state in STATES:
            for platform, width, height in (("macos", 80, 64), ("linux", 48, 48)):
                run("rsvg-convert", "--width", width, "--height", height,
                    "--output", staging / platform / "tray" / f"{state}.png",
                    ASSETS / platform / "tray" / f"{state}.svg")
            frames = []
            for size in TRAY_SIZES:
                frame = staging / f"{state}-{size}.png"
                run("rsvg-convert", "--width", size, "--height", size, "--output", frame,
                    ASSETS / "linux" / "tray" / f"{state}.svg")
                frames.append(frame)
            run("magick", *frames, staging / "win" / "tray" / f"{state}.ico")

        # Replace checked-in resources only after every conversion succeeds.
        for platform in ("macos", "linux", "win"):
            for source in sorted((staging / platform).rglob("*")):
                if source.is_file():
                    destination = ASSETS / platform / source.relative_to(staging / platform)
                    shutil.copy2(source, destination)
                    print(destination.relative_to(ASSETS))


if __name__ == "__main__":
    main()
