# droplet.png (== meat.png) actually holds TWO icons side by side: droplet (left) + meat (right).
# Split on the empty-alpha column gap; left -> droplet, right -> meat.
from PIL import Image
import os

SRC = r"C:\Users\MARU\Downloads\png\droplet.png"
PROJ = r"C:\Users\MARU\Code Locks\ReviveUI\Assets\KnockdownUI\Icons"
REPO = r"C:\Users\MARU\KnockdownPlugin\unity\KnockdownUI\Icons"

im = Image.open(SRC).convert("RGBA")
a = im.getchannel("A")
w, h = im.size
bbox = a.getbbox()
x0, y0, x1, y1 = bbox
px = a.load()
# column alpha presence within the bbox
colhas = [any(px[x, y] > 16 for y in range(y0, y1, 3)) for x in range(w)]
# find gap (run of empty columns) between x0 and x1
gaps = []
run = None
for x in range(x0, x1):
    if not colhas[x]:
        if run is None: run = [x, x]
        else: run[1] = x
    else:
        if run is not None: gaps.append(tuple(run)); run = None
if run: gaps.append(tuple(run))
# pick the widest gap roughly in the middle
gaps = [g for g in gaps if (g[1]-g[0]) > 8]
print("gaps:", gaps)
split = (x0 + x1)//2
if gaps:
    g = max(gaps, key=lambda gg: gg[1]-gg[0])
    split = (g[0]+g[1])//2
print("split x:", split)

def save_part(box, name):
    crop = im.crop(box)
    a2 = crop.getchannel("A")
    bb = a2.point(lambda v: 255 if v > 64 else 0).getbbox()
    if not bb: print("EMPTY", name); return
    sil = Image.new("RGBA", crop.size, (255,255,255,0)); sil.putalpha(a2)
    sil = sil.crop(bb)
    cw, ch = sil.size; pad = int(max(cw,ch)*0.10); s = max(cw,ch)+2*pad
    sq = Image.new("RGBA",(s,s),(255,255,255,0)); sq.paste(sil, ((s-cw)//2,(s-ch)//2))
    sq = sq.resize((128,128), Image.LANCZOS)
    for d in (PROJ, REPO): sq.save(os.path.join(d, name+".png"))
    print("saved", name, sil.size)

save_part((0, 0, split, h), "droplet")
save_part((split, 0, w, h), "meat")
