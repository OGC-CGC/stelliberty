from pathlib import Path

application = Path(defines["app"])

format = "UDZO"
filesystem = "HFS+"
files = [str(application)]
symlinks = {"Applications": "/Applications"}
icon = str(application / "Contents" / "Resources" / "AppIcon.icns")
background = defines["background"]

# 为 640 × 400 的背景预留约 28 点标题栏高度。
window_rect = ((100, 100), (640, 428))
default_view = "icon-view"
show_toolbar = False
show_status_bar = False
show_sidebar = False
show_tab_view = False
show_pathbar = False
arrange_by = None
icon_size = 96
text_size = 14
icon_locations = {
    application.name: (160, 200),
    "Applications": (480, 200),
}
