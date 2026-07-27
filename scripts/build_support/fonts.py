import hashlib
import io
import os
import re
import urllib.request
import zipfile
from dataclasses import dataclass
from pathlib import Path

from build_support.paths import ROOT

FONT_DIR = ROOT / "src" / "SBproxy.Desktop" / "Assets" / "fonts"
GOOGLE_FONT_CSS = "https://fonts.googleapis.com/css2?family=Google+Sans"
GITHUB_URL_PREFIX = os.environ.get("NETPROXY_GITHUB", "")
GOOGLE_SANS = "GoogleSans-Regular.ttf"
NOTO_SANS_SC = "NotoSansSC-VF.ttf"
MAPLE_MONO_ARCHIVE_URL = (
    f"{GITHUB_URL_PREFIX}https://github.com/subframe7536/maple-font/"
    "releases/download/v7.9/MapleMono-CN.zip"
)
MAPLE_MONO_ARCHIVE_SHA256 = "cb1e79b2c23dff772ae351784ef2b84454a61b3920e9b20bd5db4bf207e4472d"
MAPLE_MONO_FILES = {
    "MapleMono-CN-Regular.ttf": (
        1024 * 1024,
        "e6338c3dca9a3bed3fb81f43c8bb8feca3b6ad54829f5a3229527d030a321dbd",
    ),
    "MapleMono-CN-SemiBold.ttf": (
        1024 * 1024,
        "320c87dc4134f0bc88ad878255af1f1957c9e7898e76638efc9a3cdf3868574e",
    ),
}


@dataclass(frozen=True)
class FontAsset:
    name: str
    url: str
    min_bytes: int


FONT_ASSETS = [
    FontAsset(GOOGLE_SANS, GOOGLE_FONT_CSS, 32 * 1024),
    FontAsset(
        NOTO_SANS_SC,
        f"{GITHUB_URL_PREFIX}https://raw.githubusercontent.com/notofonts/noto-cjk/main/Sans/Variable/TTF/Subset/NotoSansSC-VF.ttf",
        1024 * 1024,
    ),
]


def ensure_app_fonts() -> list[Path]:
    paths: list[Path] = []
    FONT_DIR.mkdir(parents=True, exist_ok=True)

    for asset in FONT_ASSETS:
        path = FONT_DIR / asset.name
        if not is_valid_font(path, asset.min_bytes):
            payload = read_font_bytes(asset)
            if len(payload) < asset.min_bytes:
                raise RuntimeError(f"Downloaded font asset is unexpectedly small: {asset.name}")
            path.write_bytes(payload)
        paths.append(path)

    paths.extend(ensure_maple_mono_fonts())
    return paths


def ensure_maple_mono_fonts() -> list[Path]:
    paths = [FONT_DIR / name for name in MAPLE_MONO_FILES]
    if all(
        is_valid_font(path, *MAPLE_MONO_FILES[path.name])
        for path in paths
    ):
        return paths

    archive_payload = read_bytes(MAPLE_MONO_ARCHIVE_URL)
    verify_sha256(archive_payload, MAPLE_MONO_ARCHIVE_SHA256, "Maple Mono archive")

    font_payloads: dict[Path, bytes] = {}
    with zipfile.ZipFile(io.BytesIO(archive_payload)) as archive:
        for path in paths:
            min_bytes, expected_sha256 = MAPLE_MONO_FILES[path.name]
            try:
                payload = archive.read(path.name)
            except KeyError as exception:
                raise RuntimeError(
                    f"Maple Mono archive does not contain {path.name}"
                ) from exception
            if len(payload) < min_bytes:
                raise RuntimeError(
                    f"Downloaded font asset is unexpectedly small: {path.name}"
                )
            verify_sha256(payload, expected_sha256, path.name)
            font_payloads[path] = payload

    for path, payload in font_payloads.items():
        path.write_bytes(payload)
    return paths


def is_valid_font(
    path: Path,
    min_bytes: int,
    expected_sha256: str | None = None,
) -> bool:
    if not path.exists() or path.stat().st_size < min_bytes:
        return False

    return expected_sha256 is None or sha256(path.read_bytes()) == expected_sha256


def verify_sha256(payload: bytes, expected: str, description: str) -> None:
    actual = sha256(payload)
    if actual != expected:
        raise RuntimeError(
            f"SHA-256 mismatch for {description}: expected {expected}, got {actual}"
        )


def sha256(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def read_font_bytes(asset: FontAsset) -> bytes:
    if asset.name == GOOGLE_SANS:
        return read_bytes(read_google_font_url(asset.url))

    return read_bytes(asset.url)


def read_google_font_url(css_url: str) -> str:
    css = read_bytes(css_url).decode("utf-8")
    match = re.search(r"url\((https://fonts\.gstatic\.com/[^)]+\.ttf)\)", css)
    if match is None:
        raise RuntimeError("Unable to resolve the Google Sans font URL")

    return match.group(1)


def read_bytes(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "app-build"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return response.read()
