import urllib.request
from dataclasses import dataclass
from io import BytesIO
from pathlib import Path
from zipfile import ZipFile

from build_support.paths import ROOT

FONT_DIR = ROOT / "src" / "Stelliberty.Desktop" / "Assets" / "fonts"
MAPLE_MONO_ARCHIVE = "https://github.com/subframe7536/maple-font/releases/download/v7.9/MapleMono-CN-unhinted.zip"
MAPLE_MONO_FILES = (
    ("MapleMono-CN-Regular.ttf", "MapleMono-CN-Regular.ttf", 16 * 1024 * 1024),
    ("MapleMono-CN-Italic.ttf", "MapleMono-CN-Italic.ttf", 16 * 1024 * 1024),
    ("LICENSE.txt", "OFL-Maple-Mono.txt", 1024),
)
LEGACY_FONT_FILES = (
    "GoogleSans-Regular.ttf",
    "MapleMono[wght].ttf",
    "MapleMono-Italic[wght].ttf",
)
NOTO_SANS_SC = "NotoSansSC-VF.ttf"


@dataclass(frozen=True)
class FontAsset:
    name: str
    url: str
    min_bytes: int


FONT_ASSETS = [
    FontAsset(
        NOTO_SANS_SC,
        "https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/Variable/TTF/Subset/NotoSansSC-VF.ttf",
        1024 * 1024,
    ),
]


def ensure_app_fonts() -> list[Path]:
    paths: list[Path] = []
    FONT_DIR.mkdir(parents=True, exist_ok=True)

    maple_paths = [FONT_DIR / asset_name for _, asset_name, _ in MAPLE_MONO_FILES]
    if any(
        not is_valid_font(path, min_bytes)
        for path, (_, _, min_bytes) in zip(maple_paths, MAPLE_MONO_FILES)
    ):
        archive_bytes = read_bytes(MAPLE_MONO_ARCHIVE)
        with ZipFile(BytesIO(archive_bytes)) as archive:
            for path, (archive_name, _, min_bytes) in zip(maple_paths, MAPLE_MONO_FILES):
                if is_valid_font(path, min_bytes):
                    continue
                payload = archive.read(archive_name)
                if len(payload) < min_bytes:
                    raise RuntimeError(f"Downloaded font asset is unexpectedly small: {path.name}")
                path.write_bytes(payload)
    paths.extend(maple_paths)

    for asset in FONT_ASSETS:
        path = FONT_DIR / asset.name
        if not is_valid_font(path, asset.min_bytes):
            payload = read_font_bytes(asset)
            if len(payload) < asset.min_bytes:
                raise RuntimeError(f"Downloaded font asset is unexpectedly small: {asset.name}")
            path.write_bytes(payload)
        paths.append(path)

    for legacy_name in LEGACY_FONT_FILES:
        legacy_path = FONT_DIR / legacy_name
        if legacy_path.exists():
            legacy_path.unlink()

    return paths


def is_valid_font(path: Path, min_bytes: int) -> bool:
    return path.exists() and path.stat().st_size >= min_bytes


def read_font_bytes(asset: FontAsset) -> bytes:
    return read_bytes(asset.url)


def read_bytes(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "app-build"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()
