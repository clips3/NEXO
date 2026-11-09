"""Render the optional CSV emitted by Nexo.WallHarness for visual inspection."""
import csv
import sys
from PIL import Image, ImageDraw

source, target = sys.argv[1:]
image = Image.new("RGB", (1100, 1000), "white")
draw = ImageDraw.Draw(image)
scale = 95


def point(x, y):
    return (round(65 + (float(x) - 6) * scale), round(935 - (float(y) - 10) * scale))


with open(source, newline="", encoding="utf-8-sig") as file:
    rows = list(csv.reader(file))
for row in rows:
    if row[0] == "C":
        draw.line((point(row[1], row[2]), point(row[3], row[4])), fill="#b8bec8", width=1)
for row in rows:
    if row[0] == "W":
        corners = [point(row[index], row[index + 1]) for index in (1, 3, 5, 7)]
        draw.polygon(corners, fill="#b797df" if row[9] == "1" else "#e7b967", outline="#603a99" if row[9] == "1" else "#a55f10")
draw.text((65, 20), "Purple: hatch   Orange: paired lines   Grey: architecture segments", fill="#20242b")
image.save(target)
