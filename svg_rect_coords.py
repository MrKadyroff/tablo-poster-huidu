#!/usr/bin/env python3
import argparse
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

RECT_RE = re.compile(
    r'<rect\s+width="(?P<width>[^"]+)"\s+height="(?P<height>[^"]+)"\s+'
    r'transform="matrix\((?P<matrix>[^\)]*)\)"\s+fill="url\(#(?P<pattern>pattern\d+_0_1)\)"\s*/>'
)


@dataclass
class RectInfo:
    pattern: str
    width: str
    height: str
    matrix_values: list[str]
    match_start: int
    match_end: int
    original_tag: str

    @property
    def x(self) -> str:
        return self.matrix_values[4]

    @property
    def y(self) -> str:
        return self.matrix_values[5]


def parse_rects(svg_text: str) -> list[RectInfo]:
    rects: list[RectInfo] = []
    for m in RECT_RE.finditer(svg_text):
        matrix_values = m.group("matrix").split()
        if len(matrix_values) != 6:
            continue
        rects.append(
            RectInfo(
                pattern=m.group("pattern"),
                width=m.group("width"),
                height=m.group("height"),
                matrix_values=matrix_values,
                match_start=m.start(),
                match_end=m.end(),
                original_tag=m.group(0),
            )
        )
    return rects


def build_updated_tag(rect: RectInfo, x: Optional[str], y: Optional[str], width: Optional[str], height: Optional[str]) -> str:
    new_matrix = rect.matrix_values[:]
    if x is not None:
        new_matrix[4] = x
    if y is not None:
        new_matrix[5] = y

    new_width = width if width is not None else rect.width
    new_height = height if height is not None else rect.height

    return (
        f'<rect width="{new_width}" height="{new_height}" '
        f'transform="matrix({" ".join(new_matrix)})" '
        f'fill="url(#{rect.pattern})"/>'
    )


def print_rects(rects: list[RectInfo]) -> None:
    print("idx pattern         x          y          width       height")
    print("--- -------------- ---------- ---------- ---------- ----------")
    for i, r in enumerate(rects):
        print(f"{i:<3} {r.pattern:<14} {r.x:<10} {r.y:<10} {r.width:<10} {r.height:<10}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Show/update image rect coordinates in image.svg")
    parser.add_argument("svg", help="Path to SVG file")
    parser.add_argument("--set-idx", type=int, help="Rect index from --list")
    parser.add_argument("--x", help="New numeric x (matrix tx)")
    parser.add_argument("--y", help="New numeric y (matrix ty)")
    parser.add_argument("--width", help="New numeric width")
    parser.add_argument("--height", help="New numeric height")
    parser.add_argument("--list", action="store_true", help="List coordinates")

    args = parser.parse_args()
    svg_path = Path(args.svg)
    text = svg_path.read_text(encoding="utf-8")
    rects = parse_rects(text)

    if not rects:
        raise SystemExit("No target image rects found.")

    if args.list or args.set_idx is None:
        print_rects(rects)
        if args.set_idx is None:
            return

    if args.set_idx < 0 or args.set_idx >= len(rects):
        raise SystemExit(f"Invalid --set-idx {args.set_idx}. Range is 0..{len(rects)-1}")

    if args.x is None and args.y is None and args.width is None and args.height is None:
        raise SystemExit("Nothing to update. Provide one of --x --y --width --height")

    target = rects[args.set_idx]
    new_tag = build_updated_tag(target, args.x, args.y, args.width, args.height)

    updated = text[:target.match_start] + new_tag + text[target.match_end:]
    svg_path.write_text(updated, encoding="utf-8")

    print(f"Updated rect idx={args.set_idx} ({target.pattern})")
    print(f"x={args.x if args.x is not None else target.x}, y={args.y if args.y is not None else target.y}, "
          f"width={args.width if args.width is not None else target.width}, "
          f"height={args.height if args.height is not None else target.height}")


if __name__ == "__main__":
    main()
