from PIL import Image
import os

ICONS = r"C:\Users\MARU\Code Locks\ReviveUI\Assets\KnockdownUI\Icons"
names = ["downed", "gun", "melee", "punch", "car", "zombie", "blood", "skull", "biohazard", "droplet", "meat"]
cell = 130
cols = 6
rows = (len(names) + cols - 1) // cols
sheet = Image.new("RGB", (cols * cell, rows * cell), (40, 42, 48))
from PIL import ImageDraw
d = ImageDraw.Draw(sheet)
for i, nm in enumerate(names):
    p = os.path.join(ICONS, nm + ".png")
    cx = (i % cols) * cell
    cy = (i // cols) * cell
    if os.path.exists(p):
        ic = Image.open(p).convert("RGBA").resize((110, 110))
        # white silhouette -> tint gold so it shows on dark
        tint = Image.new("RGBA", ic.size, (235, 185, 74, 255))
        tint.putalpha(ic.getchannel("A"))
        sheet.paste(tint, (cx + 10, cy + 6), tint)
        # opaque coverage %
        a = ic.getchannel("A")
        cov = sum(1 for p2 in a.getdata() if p2 > 128) * 100 // (110 * 110)
        d.text((cx + 6, cy + cell - 16), nm + " " + str(cov) + "%", fill=(200, 200, 200))
    else:
        d.text((cx + 6, cy + 50), "MISSING " + nm, fill=(255, 100, 100))
out = r"C:\Users\MARU\unturned_mod_export\killfeed_icons_preview.png"
sheet.save(out)
print(out)
