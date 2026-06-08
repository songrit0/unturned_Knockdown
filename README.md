# Knockdown — Unturned RocketMod Plugin

Down-but-not-out / revive system.

| | |
|---|---|
| Unturned | 3.26.3.2 |
| Rocket.Unturned | 4.9.3.18 |
| Target framework | .NET Framework 4.8 |
| FPlugins bridge | Compatible (standard RocketMod plugin, no FPlugins-specific API used) |

> Verified: the source in this folder compiles **clean (0 errors, 0 warnings, no deprecated API)** against the real
> `Assembly-CSharp.dll` (3.26.3.2) and `Rocket.*.dll` (4.9.3.18) on this machine.

---

## ⬇️ Download (server owners)

You do **not** need to build anything — a ready-to-use compiled DLL is included in this repo.

**[➡️ Download `Knockdown.dll`](https://github.com/songrit0/unturned_Knockdown/raw/main/dist/Knockdown.dll)**

Then drop it on your server:

```
<server>/Rocket/Plugins/Knockdown/Knockdown.dll
```

Start the server once (RocketMod generates `Knockdown.configuration.xml`), edit the config, then `/knockdownreload`.
Full steps are in [Installation](#installation) below. Players install nothing — it's 100% server-side.

---

## Files

| File | Purpose |
|------|---------|
| `Knockdown.cs` | Main plugin: damage interception, downed state, timer, revive channel, cleanup |
| `KnockdownConfiguration.cs` | Config model (auto-serialized by RocketMod) |
| `CommandKnockdownReload.cs` | `/knockdownreload` admin command |
| `CommandKnockdown.cs` | `/knockdown on\|off\|status` — players opt themselves in/out of the system |
| `CommandKnockMe.cs` | `/knockme` test command — forces the caller into knockdown |
| `plugin.xml` | Human-readable metadata manifest |
| `Knockdown.csproj` | SDK-style build project (needs the .NET SDK; see notes) |

---

## How it works

1. **Death prevention** — subscribes to `DamageTool.damagePlayerRequested`. If incoming damage (`damage * times`)
   would reach the player's current health, the death is cancelled (`shouldAllow = false`) and the player is downed.
2. **Downed state** — HP set to `KnockHealth`, bleeding/broken cleared, movement clamped to `CrawlSpeed`
   (`PlayerMovement.sendPluginSpeedMultiplier`), the **SIT/rest** pose applied (best-effort), held item dequipped and all
   equip requests blocked via `PlayerEquipment.onEquipRequested` (this disables shooting, weapon equip and item use).
   Effect `KnockEffectID` is played.
3. **Bleed-out** — for the first `InvincibleDuration` seconds the player is immune and HP holds at `KnockHealth`.
   After that, HP drains along a linear curve toward 0 in `FixedUpdate`; when it reaches 0 (or the `KnockDuration`
   lifetime ends) the player dies normally (`PlayerLife.askDamage`, crediting the original attacker/cause).
4. **Revive** — every tick the plugin scans `Provider.clients` for a player who is within `ReviveDistance`, alive, not
   themselves downed, and **performing the revive input** (crouch by default). One reviver "claims" a target; holding for
   `ReviveDuration` seconds restores `ReviveHealth` HP, plays `ReviveEffectID`, and clears the downed state. A per-second
   progress message + `ReviveSoundEffectID` play during the channel. Leaving range or releasing cancels it (another
   player may then claim it). Anyone can revive — no group restriction.
5. **Re-downed damage** — during the grace period all damage is blocked. After grace, if `InvincibleWhileDowned` is
   `false`, combat damage subtracts from the draining HP (and can finish the player early); environmental ticks
   (bleed/starve/etc.) are always ignored.
6. **Cleanup** — `U.Events.OnPlayerDisconnected` and `Unload()` tear down state, unsubscribe the per-player equip
   handler, and reset the speed multiplier. No leaked handlers, no leaked dictionary entries.

All logic runs on the Unity main thread, so simultaneous revives / multiple downed players are handled without locks.

---

## Revive input

There are three modes you can set on `ReviveInput`:

| Mode | How it feels in-game |
|---|---|
| `CROUCH` (default) | Hold crouch (ย่อ) next to the downed player for the entire `ReviveDuration`. Stand up = revive cancels. |
| `CROUCH_START` | Press crouch **once** near the downed player to start; afterwards you can stand up and walk around freely — the revive only cancels if you leave `ReviveDistance`. |
| `PLUGINKEY` | Hold a bound Unturned **Plugin Key** (`Options → Controls → "Plugin Key 1"`). `RevivePluginKeyIndex` is **0-based** (`0` = "Plugin Key 1"). |

Crouch-based modes are fully server-side and need no key binding. The server can't read a raw F press, so F only works
through the Plugin Key binding.

## Visual feedback

The plugin can show two purely-cosmetic effects so teammates can spot a downed player:

- **Range ring** (`RangeEffect*`) — a horizontal ring of effect points drawn on the ground around the downed player,
  matching the revive distance. Continuously visible from the moment the player is downed until they recover or die.
- **Sky flare** (`KnockFlare*`) — a signal-flare effect that rises from the downed player into the sky in a vertical
  column, then "hangs" at the peak (optionally as a ring) so distant teammates can see the location from far away.

Both default ON. Set their `…EffectID` to `0` to disable independently.

---

## Build instructions (Visual Studio)

### Option A — Visual Studio, manual references (most reliable for RocketMod)

1. **File → New → Project → Class Library (.NET Framework)**, framework **.NET Framework 4.8**, name **Knockdown**.
2. Delete the auto-generated `Class1.cs`. Add the four `.cs` files from this folder to the project.
3. **Project → Add Reference… → Browse**, and add these 7 DLLs:

   From `…\Unturned\Unturned_Data\Managed\`:
   - `Assembly-CSharp.dll`
   - `UnityEngine.dll`
   - `UnityEngine.CoreModule.dll`
   - `com.rlabrecque.steamworks.net.dll`  *(defines `Steamworks.CSteamID`)*

   From `…\Unturned\Extras\Rocket.Unturned\`:
   - `Rocket.API.dll`
   - `Rocket.Core.dll`
   - `Rocket.Unturned.dll`

4. For **every** reference above, set **Copy Local = False** (do not ship the game's DLLs with your plugin).
5. Set build configuration to **Release**, then **Build → Build Solution**.
6. Output: `bin\Release\Knockdown.dll`.

> On this machine the DLLs are at:
> `D:\SteamLibrary\steamapps\common\Unturned\Unturned_Data\Managed`
> `D:\SteamLibrary\steamapps\common\Unturned\Extras\Rocket.Unturned`

### Option B — provided `Knockdown.csproj`

`Knockdown.csproj` is SDK-style and already references the 7 DLLs by absolute path. It builds with the **.NET SDK**:

```powershell
dotnet build .\Knockdown.csproj -c Release
```

> This machine currently has only the .NET *runtime* (no SDK) and VS MSBuild without the .NET SDK component, so
> Option A (or installing the .NET SDK from https://aka.ms/dotnet/download) is the path to build here.

---

## Installation

Copy the compiled DLL to the plugin folder (folder name must match the assembly name):

```
Servers/MyServer/Rocket/Plugins/Knockdown/Knockdown.dll
```

Start the server once. RocketMod auto-generates:

```
Servers/MyServer/Rocket/Plugins/Knockdown/Knockdown.configuration.xml
```

Edit it, then either restart or run `/knockdownreload` in-game.

### Permissions

Grant the reload command in `Rocket/Permissions.config.xml` (admins with `*` already have it):

```xml
<Permission Cooldown="0">knockdown.reload</Permission>
```

---

## Configuration (`Knockdown.configuration.xml`)

Auto-generated example with defaults:

```xml
<?xml version="1.0" encoding="utf-8"?>
<KnockdownConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <KnockDuration>60</KnockDuration>
  <ReviveDuration>8</ReviveDuration>
  <ReviveHealth>25</ReviveHealth>
  <KnockHealth>100</KnockHealth>
  <InvincibleDuration>3</InvincibleDuration>
  <CrawlSpeed>0.25</CrawlSpeed>
  <KnockEffectID>61</KnockEffectID>
  <ReviveEffectID>61</ReviveEffectID>
  <ReviveSoundEffectID>56</ReviveSoundEffectID>
  <RangeEffectID>130</RangeEffectID>
  <RangeEffectInterval>0.5</RangeEffectInterval>
  <RangeEffectPoints>16</RangeEffectPoints>
  <RangeEffectYOffset>-0.5</RangeEffectYOffset>
  <KnockFlareEffectID>125</KnockFlareEffectID>
  <KnockFlareHeight>50</KnockFlareHeight>
  <KnockFlareDuration>1.5</KnockFlareDuration>
  <KnockFlareSteps>10</KnockFlareSteps>
  <KnockFlareHangDuration>5</KnockFlareHangDuration>
  <KnockFlareHangInterval>0.3</KnockFlareHangInterval>
  <KnockFlareHangRingRadius>6</KnockFlareHangRingRadius>
  <KnockFlareHangRingPoints>8</KnockFlareHangRingPoints>
  <ReviveDistance>4</ReviveDistance>
  <ReviveInput>CROUCH</ReviveInput>
  <DownedPose>SIT</DownedPose>
  <RevivePluginKeyIndex>0</RevivePluginKeyIndex>
  <InvincibleWhileDowned>false</InvincibleWhileDowned>
  <PauseDrainWhileReviving>true</PauseDrainWhileReviving>
  <ReviverGesture>POINT</ReviverGesture>
  <DownedHpMessageInterval>5</DownedHpMessageInterval>
  <ReviveProgressMessageInterval>2</ReviveProgressMessageInterval>
  <AllowPlayerOptOut>true</AllowPlayerOptOut>
  <EnableItemRevive>true</EnableItemRevive>
  <ItemReviveIds>
    <unsignedShort>15</unsignedShort>   <!-- Medkit -->
    <unsignedShort>95</unsignedShort>   <!-- Bandage -->
    <unsignedShort>96</unsignedShort>   <!-- Splint -->
    <unsignedShort>388</unsignedShort>  <!-- Morphine -->
    <unsignedShort>394</unsignedShort>  <!-- Dressing -->
    <unsignedShort>395</unsignedShort>  <!-- Bloodbag -->
  </ItemReviveIds>
  <MessageKnocked Text="If knocked down, wait for a teammate to revive you" Color="white" />
  <MessageRevived Text="You have been revived" Color="green" />
  <MessageBeingRevived Text="A teammate is reviving you" Color="green" />
  <MessageReviveCancelled Text="Revive cancelled" Color="red" />
  <MessageReviveStarted Text="Reviving... stay crouched and close" Color="yellow" />
  <MessageReviveProgress Text="Reviving... {seconds}s left ({percent}%)" Color="yellow" />
  <MessageDownedHp Text="Bleeding out... HP {hp} ({seconds}s left)" Color="red" />
  <MessageKnockdownDisabled Text="Knockdown disabled for you — you will die normally" Color="yellow" />
  <MessageKnockdownEnabled Text="Knockdown enabled for you" Color="green" />
</KnockdownConfiguration>
```

### Core gameplay

| Key | Default | Meaning |
|-----|---------|---------|
| `KnockDuration` | 60 | Total downed lifetime in seconds (HP reaches 0 at the end) |
| `ReviveDuration` | 8 | Seconds the revive input must be held |
| `ReviveHealth` | 25 | HP restored on revive (0–100) |
| `KnockHealth` | 100 | Starting HP when downed; drains toward 0 over time (0–100) |
| `InvincibleDuration` | 3 | Grace seconds at start: immune + HP held before draining begins |
| `CrawlSpeed` | 0.25 | Movement multiplier while downed (1.0 = normal). Note: high values may be capped by the forced pose — pair with `DownedPose = CROUCH` if you want noticeably-faster downed movement. |
| `ReviveDistance` | 4 | Max metres between reviver and downed player. Also the radius of the range ring. |
| `ReviveInput` | CROUCH | `CROUCH`, `CROUCH_START`, or `PLUGINKEY` — see [Revive input](#revive-input). |
| `DownedPose` | SIT | Downed pose: `SIT`/`REST`, `CROUCH`, or `PRONE` |
| `RevivePluginKeyIndex` | 0 | 0-based plugin key slot, used only when `ReviveInput = PLUGINKEY` |
| `InvincibleWhileDowned` | false | After grace: `true` = stay immune (only HP drain kills); `false` = combat damage can finish them early |
| `PauseDrainWhileReviving` | true | `true` = HP bleed-out + timer pause while a revive is in progress |
| `ReviverGesture` | POINT | Gesture the reviver plays while channeling. Any `EPlayerGesture` (`POINT`, `WAVE`, `SALUTE`, `FACEPALM`, …) or `NONE`. |
| `AllowPlayerOptOut` | true | `true` = players may disable knockdown for themselves with `/knockdown off` (persisted). `false` = force the system on for everyone and disable the command. |
| `EnableItemRevive` | true | `true` = a nearby player can **instantly** revive a downed teammate by **using (right-click) a medical item** from `ItemReviveIds`. Works alongside the crouch-channel revive. |
| `ItemReviveIds` | 15, 95, 96, 388, 394, 395 | Item ids that act as instant-revive tools (vanilla Medkit/Bandage/Splint/Morphine/Dressing/Bloodbag). Replace with your server's healing item ids. |

### Item-based instant revive

When `EnableItemRevive = true`, any alive player who **uses** (right-click / consumes) an item whose id is in
`ItemReviveIds` while standing within `ReviveDistance` of a downed player **instantly revives the closest one** — no
crouch channel needed. The downed player comes back at **that item's own heal value** (e.g. a Medkit revives to 75 HP,
a Bandage to 15 HP). The item is consumed normally by the game — that is the cost. If nobody is downed in range, the
item just heals the user as usual.

> ⚠️ Vanilla blocks consuming a pure-heal item at **full health with no injuries**, so a completely-healthy reviver may
> be unable to pop a bandage to trigger this. In practice revivers are usually hurt; if not, the crouch-channel revive
> still works. There is **no team check** (consistent with the crouch revive — anyone can revive anyone in range).

### One-shot effects

| Key | Default | Meaning |
|-----|---------|---------|
| `KnockEffectID` | 61 | Effect asset id played once on knockdown |
| `ReviveEffectID` | 61 | Effect asset id played once on revive |
| `ReviveSoundEffectID` | 56 | Sound effect played once/sec while reviving (vanilla "Beep"; 0 = off) |

### Range ring (around the downed player)

A horizontal ring of effect points drawn at the downed player's feet, continuously, so teammates can see the revive area.

| Key | Default | Meaning |
|-----|---------|---------|
| `RangeEffectID` | 130 | Effect asset id for ring points. `0` = disable the ring. |
| `RangeEffectInterval` | 0.5 | Seconds between ring bursts (smaller = smoother, more network traffic) |
| `RangeEffectPoints` | 16 | Number of points distributed around the circle |
| `RangeEffectYOffset` | -0.5 | Vertical offset relative to the player; negative sinks the ring closer to the ground |

Radius = `ReviveDistance` (so the ring always matches the actual revive range).

### Sky flare (vertical signal pillar)

When a player is knocked, a column of effects rises from them into the sky, then hangs at the peak.

| Key | Default | Meaning |
|-----|---------|---------|
| `KnockFlareEffectID` | 125 | Effect asset id for the flare. `0` = disable the entire flare animation. |
| `KnockFlareHeight` | 50 | Peak height in metres above the downed player |
| `KnockFlareDuration` | 1.5 | Seconds for the flare to travel from ground to peak |
| `KnockFlareSteps` | 10 | Effect points spawned along the trajectory (more = smoother trail) |
| `KnockFlareHangDuration` | 5 | After reaching the peak, keep re-triggering at the top for this many seconds. `0` = no hang. |
| `KnockFlareHangInterval` | 0.3 | Seconds between re-triggers while hanging |
| `KnockFlareHangRingRadius` | 6 | If `> 0`, the hang phase draws a horizontal ring of this radius at the peak instead of a single point |
| `KnockFlareHangRingPoints` | 8 | Number of effect points around the hang ring (only used when `KnockFlareHangRingRadius > 0`) |

### Chat message cadence

| Key | Default | Meaning |
|-----|---------|---------|
| `DownedHpMessageInterval` | 5 | Seconds between `MessageDownedHp` repeats to the downed player. Larger = less spam. |
| `ReviveProgressMessageInterval` | 2 | Seconds between `MessageReviveProgress` repeats to reviver + downed player |

`MessageReviveProgress` placeholders: `{seconds}`, `{total}`, `{percent}`.
`MessageDownedHp` placeholders: `{hp}`, `{seconds}`.

**Disable any message** by setting its `Text=""` (the plugin skips empty messages entirely).

### Effect ids

Effects (`KnockEffectID`, `RangeEffectID`, `KnockFlareEffectID`, etc.) are Unturned **EffectAsset** ids. A few commonly-used vanilla ids:

| id | What it is |
|---|---|
| 17 | Smoke |
| 56 | "Beep" sound |
| 61 | Generic small explosion |
| 125 | Signal flare |
| 128 | Generic ground puff (used elsewhere as a "stored" cue) |
| 130 | Used as the default range-ring marker |
| 142 / 143 / 144 / 145 | Green / blue / red / yellow flares |

Always verify on your own server (asset ids can shift between Unturned versions); the plugin logs a warning and skips if an id isn't an `EffectAsset`.

### Message colours

Every message has a `Text` and a `Color` attribute:

```xml
<MessageRevived Text="You have been revived" Color="green" />
```

`Color` accepts a colour **name** (`white`, `red`, `green`, `yellow`, `cyan`, `blue`, …) or a **hex** value (`#00FF7F`).
An unknown value falls back to white.

**Multiple colours in one message** — wrap parts of `Text` in TMP rich-text tags (escape `<`/`>` as `&lt;`/`&gt;` in XML);
the `Color` attribute is just the base colour for untagged text:

```xml
<MessageRevived Text="&lt;color=#00FF7F&gt;You have been revived&lt;/color&gt; &lt;color=yellow&gt;+50 HP&lt;/color&gt;" Color="white" />
```

Supported tags include `<color=…>`, `<b>`, `<i>`, `<size=…>`. After editing, run `/knockdownreload`.

---

## Commands

| Command | Alias | Permission | Description |
|---------|-------|------------|-------------|
| `/knockdownreload` | `/kdreload` | `knockdown.reload` | Reload the configuration from disk |
| `/knockdown on\|off\|status` | `/kd …` | `knockdown.optout` | Player opts their **own** character in/out of the system. `off` = die normally (never get downed); `on` = re-enable; no argument / `status` = show current state. The choice is **persisted across sessions/restarts** in `Knockdown.optout.txt`. Calling `off` while currently downed kills you immediately. Disabled server-wide if `AllowPlayerOptOut = false`. |
| `/knockme` | `/testknock` | `knockdown.test` | Force yourself into knockdown immediately, bypassing the damage pipeline. Useful for testing the full visual (range ring, sky flare, downed pose, revive flow) without dying. |

Grant in `Rocket/Permissions.config.xml`:

```xml
<Permission Cooldown="0">knockdown.reload</Permission>
<Permission Cooldown="0">knockdown.test</Permission>
```

Admins with `*` already have all of them. To let **every** player opt themselves out, grant
`knockdown.optout` to your **default group** (the group every player belongs to):

```xml
<!-- inside the default <Group> ... <Permissions> in Permissions.config.xml -->
<Permission Cooldown="0">knockdown.optout</Permission>
```

---

## Upgrading from an older config

RocketMod does **not** retro-fill missing fields when a `Knockdown.configuration.xml` already exists on disk — they
deserialize to the C# default (`0` for numeric / `null` for strings), **not** to the values in `LoadDefaults()`.

When you upgrade to a build that adds new fields, either:

- **Edit `Knockdown.configuration.xml`** and add the new entries manually (using the values from the table above as a
  starting point), then `/knockdownreload`; or
- **Delete the file** and let RocketMod regenerate a fresh one with every default populated. Your message text and any
  hand-tuned numbers will be lost.

For example: after upgrading to a build that introduced `RangeEffectID` / `KnockFlareEffectID`, an old config that
doesn't list them will produce `0` values (i.e. ring + flare silently disabled). Add the entries and reload.

> ⚠️ **`AllowPlayerOptOut`** is a `bool`, so a config that predates it deserializes to `false` — silently disabling
> the `/knockdown` opt-out command. After upgrading, add `<AllowPlayerOptOut>true</AllowPlayerOptOut>` to your existing
> `Knockdown.configuration.xml` (or delete the file to regenerate it) and reload.

### Per-player opt-out storage

`/knockdown off`/`on` choices are stored separately from the config, in `Knockdown.optout.txt` next to the plugin DLL —
a plain text file with **one SteamID per line** (`#` comments and blank lines are ignored). This file is **player data**:
it survives config edits, and deleting/regenerating `Knockdown.configuration.xml` does **not** touch it. Delete
`Knockdown.optout.txt` to reset everyone back into the system.

## Implementation notes & known trade-offs

- **Lethality check ignores armour.** Downing triggers when `damage * times >= currentHealth`. Replicating Unturned's
  exact armour math at the pre-damage hook isn't feasible; worst case a heavily-armoured player is downed slightly early
  rather than killed. This is the standard approach for downed-state plugins.
- **Prone is best-effort.** Stance is client-authoritative, so `checkStance(PRONE)` is re-asserted once per second but may
  not always stick visually. Movement restriction (crawl) and combat lockout are fully enforced server-side.
- **Effect ids must exist** as EFFECT assets on the server, otherwise a warning is logged and the effect is skipped.
- **Network traffic from visuals.** Each ring point and each flare step is a separate effect packet. The defaults
  (16-point ring at 0.5s + 8-point hang ring at 0.3s + 10-step rise) are tuned for a handful of simultaneously-downed
  players. Servers with many concurrent downs can lower `RangeEffectPoints`, raise `RangeEffectInterval`, or shorten
  `KnockFlareHangDuration` to reduce broadcasts.
- **`/knockme` bypasses the damage path** by calling `ForceKnockdown` directly. This avoids interference from armour,
  god-mode plugins, or other damage-event subscribers — so it's a faithful test of the downed-state visuals and revive
  flow, but it does **not** validate the damage-interception code path. To test that, take damage normally instead.
- Compiled artifact from the verification build is in `bin\Knockdown.dll` (built with the framework Roslyn compiler);
  prefer rebuilding via Visual Studio for distribution.
