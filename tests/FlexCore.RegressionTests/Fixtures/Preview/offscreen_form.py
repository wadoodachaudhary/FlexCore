"""Tiny offscreen preview: a red frame that turns green after a pointer release."""
import os
import sys

path = os.environ["FLEXCORE_PREVIEW_FRAME"]
width, height = 80, 40
color = bytes([255, 0, 0])


def write() -> None:
    payload = b"FCFR" + width.to_bytes(4, "big") + height.to_bytes(4, "big") + color * (width * height)
    temporary = path + ".tmp"
    with open(temporary, "wb") as handle:
        handle.write(payload)
    os.replace(temporary, path)


write()
for line in sys.stdin:
    if line.startswith("up "):
        color = bytes([0, 128, 0])
        write()
