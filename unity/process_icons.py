# Clean killfeed icons: take the ALPHA channel as the silhouette (icons are opaque on
# transparent bg), recolour white, autocrop, square, 128px. -> project + repo Icons.
from PIL import Image
import os

SRC = r"C:\Users\MARU\Downloads\png"
PROJ = r"C:\Users\MARU\Code Locks\ReviveUI\Assets\KnockdownUI\Icons"
REPO = r"C:\Users\MARU\KnockdownPlugin\unity\KnockdownUI\Icons"
for d in (PROJ, REPO): os.makedirs(d, exist_ok=True)

NAMES = ["downed","gun","melee","punch","car","zombie","blood","skull","biohazard","droplet","meat"]

for nm in NAMES:
    p = os.path.join(SRC, nm + ".png")
    if not os.path.exists(p):
        print("MISSING", nm); continue
    im = Image.open(p).convert("RGBA")
    a = im.getchannel("A")
    # white silhouette with the icon's own alpha (anti-aliased edges, tintable)
    white = Image.new("RGBA", im.size, (255, 255, 255, 0))
    white.putalpha(a)
    # crop to the SOLID region only (ignore faint anti-alias/glow that skews centering)
    bbox = a.point(lambda v: 255 if v > 64 else 0).getbbox()
    if not bbox:
        print("EMPTY", nm); continue
    crop = white.crop(bbox)
    cw, ch = crop.size
    pad = int(max(cw, ch) * 0.10)
    s = max(cw, ch) + 2 * pad
    sq = Image.new("RGBA", (s, s), (255, 255, 255, 0))
    sq.paste(crop, ((s - cw) // 2, (s - ch) // 2))
    sq = sq.resize((128, 128), Image.LANCZOS)
    for d in (PROJ, REPO): sq.save(os.path.join(d, nm + ".png"))
    print("  saved", nm, "crop", crop.size)
print("DONE ->", PROJ)
