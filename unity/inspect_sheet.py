from PIL import Image
import sys

src = r"C:\Users\MARU\Downloads\ChatGPT Image 10 มิ.ย. 2569 23_34_35.png"
# fall back: glob the 23_34_35 file
import glob, os
hits = glob.glob(r"C:\Users\MARU\Downloads\*23_34_35*.png")
if hits:
    src = hits[0]
print("src:", src)
im = Image.open(src).convert("L")
w, h = im.size
print("size:", w, h)
px = im.load()

# row brightness profile (find the 2 icon rows by where bright pixels cluster)
def bright_count_row(y, T):
    return sum(1 for x in range(0, w, 4) if px[x, y] > T)
def bright_count_col(x, T):
    return sum(1 for y in range(0, h, 4) if px[x, y] > T)

T = 40
rows = [(y, bright_count_row(y, T)) for y in range(0, h, 16)]
cols = [(x, bright_count_col(x, T)) for x in range(0, w, 16)]
print("rows with >5 bright (y,count):", [(y, c) for y, c in rows if c > 5])
print("cols with >5 bright (x,count):", [(x, c) for x, c in cols if c > 5])
# overall extrema
print("extrema:", im.getextrema())
