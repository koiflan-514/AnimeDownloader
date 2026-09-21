#!/usr/bin/env python3
"""生成 Android 启动图标的各密度位图。

为什么需要这个脚本，而不是直接放几个 PNG 进仓库：

  · 自适应图标（API 26+）是矢量 XML，由 `Resources/mipmap-anydpi-v26/ic_launcher.xml`
    引用 `Resources/drawable/ic_launcher_foreground.xml`。那部分是手写的，改动肉眼可审。
  · 但 API 24–25 必须吃**位图**，而桌面端仓库里那张 `AnimeDownloader.png` 只有 64×64 ——
    放大到 xxxhdpi 需要的 192×192 只会得到一团糊。所以这里按与矢量**同一套几何比例**
    重新绘制，保证新旧图标是同一个标记，而不是「一张糊图 + 一张清晰的图」并存。

用法（无第三方依赖以外的要求，只需要 Pillow）：

    python tools/IconGen/gen_launcher_icons.py

输出直接写进 src/AnimeDownloader.App/Resources/mipmap-*/。脚本是幂等的，重跑覆盖同几个文件。
"""

from __future__ import annotations

import pathlib
import sys

from PIL import Image, ImageDraw

# 令牌与 colors.xml / Dk.cs 保持一致
BACKGROUND = (0x0D, 0x0E, 0x11, 255)  # dk_window_background
INK = (0xED, 0xEE, 0xF1, 255)  # AppTextPrimaryBrush

# 每个密度档的边长（dp × 密度倍数）
DENSITIES = {"mdpi": 48, "hdpi": 72, "xhdpi": 96, "xxhdpi": 144, "xxxhdpi": 192}

# 超采样倍数：先按大图绘制再缩小，得到抗锯齿边缘（Pillow 没有绘制级 AA）
SUPERSAMPLE = 8

# 标记的几何比例（相对图标边长），与 ic_launcher_foreground.xml 同源
SHAFT_LEFT, SHAFT_RIGHT = 0.455, 0.545
HEAD_LEFT, HEAD_RIGHT = 0.320, 0.680
TOP, SHOULDER, TIP = 0.175, 0.500, 0.680
BASE_LEFT, BASE_RIGHT, BASE_TOP, BASE_BOTTOM = 0.300, 0.700, 0.745, 0.800

CORNER_RADIUS_RATIO = 0.22


def _draw_mark(draw: ImageDraw.ImageDraw, side: int) -> None:
    """把「落到托盘里的箭头」画到已铺好底色的画布上。"""
    draw.polygon(
        [
            (int(side * SHAFT_LEFT), int(side * TOP)),
            (int(side * SHAFT_RIGHT), int(side * TOP)),
            (int(side * SHAFT_RIGHT), int(side * SHOULDER)),
            (int(side * HEAD_RIGHT), int(side * SHOULDER)),
            (int(side * 0.5), int(side * TIP)),
            (int(side * HEAD_LEFT), int(side * SHOULDER)),
            (int(side * SHAFT_LEFT), int(side * SHOULDER)),
        ],
        fill=INK,
    )
    draw.rectangle(
        [
            int(side * BASE_LEFT),
            int(side * BASE_TOP),
            int(side * BASE_RIGHT),
            int(side * BASE_BOTTOM),
        ],
        fill=INK,
    )


def render(size: int, *, round_shape: bool) -> Image.Image:
    """渲染单个图标（超采样后缩小）。"""
    side = size * SUPERSAMPLE
    image = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    if round_shape:
        draw.ellipse([0, 0, side - 1, side - 1], fill=BACKGROUND)
    else:
        draw.rounded_rectangle(
            [0, 0, side - 1, side - 1],
            radius=int(side * CORNER_RADIUS_RATIO),
            fill=BACKGROUND,
        )

    _draw_mark(draw, side)
    return image.resize((size, size), Image.LANCZOS)


def main() -> int:
    repo_root = pathlib.Path(__file__).resolve().parents[2]
    res = repo_root / "src" / "AnimeDownloader.App" / "Resources"
    if not res.is_dir():
        print(f"找不到资源目录：{res}", file=sys.stderr)
        return 1

    written = 0
    for density, size in DENSITIES.items():
        target = res / f"mipmap-{density}"
        target.mkdir(parents=True, exist_ok=True)
        for name, round_shape in (("ic_launcher.png", False), ("ic_launcher_round.png", True)):
            path = target / name
            render(size, round_shape=round_shape).save(path)
            written += 1
            print(f"{path.relative_to(repo_root)}  {size}x{size}")

    # 自适应图标（API 26+）是矢量，不在这里生成 —— 只是确认它们还在。
    adaptive = res / "mipmap-anydpi-v26" / "ic_launcher.xml"
    if not adaptive.is_file():
        print(f"警告：缺少自适应图标 {adaptive}", file=sys.stderr)

    print(f"完成，写入 {written} 个文件")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
