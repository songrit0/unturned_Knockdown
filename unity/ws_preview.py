from PIL import Image, ImageDraw
import os

ICONS = r"C:\Users\MARU\Code Locks\ReviveUI\Assets\KnockdownUI\Icons"
out = r"C:\Users\MARU\unturned_mod_export\knockdownui_preview.png"
W = 512
img = Image.new("RGB", (W, W), (24, 26, 31))
d = ImageDraw.Draw(img)
d.rectangle([0, 0, W, 96], fill=(16, 17, 22))
d.text((24, 30), "KNOCKDOWN  UI", fill=(235, 185, 74))
d.text((24, 52), "revive HUD + killfeed", fill=(150, 155, 165))

picks = [("downed", (235,185,74)), ("gun", (255,255,255)), ("melee", (235,235,235)),
         ("zombie", (125,207,107)), ("blood", (239,106,106)), ("biohazard", (153,205,50)),
         ("droplet", (92,168,245)), ("meat", (217,163,74)), ("skull", (205,205,210)),
         ("car", (158,181,209)), ("punch", (235,235,235))]
cell = 100
cols = 4
for i, (nm, col) in enumerate(picks):
    p = os.path.join(ICONS, nm + ".png")
    cx = 20 + (i % cols) * ((W-40)//cols)
    cy = 120 + (i // cols) * cell
    if os.path.exists(p):
        ic = Image.open(p).convert("RGBA").resize((72, 72))
        tint = Image.new("RGBA", ic.size, col + (255,))
        tint.putalpha(ic.getchannel("A"))
        img.paste(tint, (cx + 12, cy), tint)
img.save(out)
print(out, os.path.getsize(out), "bytes")
