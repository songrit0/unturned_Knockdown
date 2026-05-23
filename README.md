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

Default (`ReviveInput = "CROUCH"`): a reviver simply **holds crouch (ย่อ) next to the downed player** for
`ReviveDuration` seconds. Crouch is a stance that Unturned syncs to the server, so this needs **no key binding** and
has no F-key issues.

Optional (`ReviveInput = "PLUGINKEY"`): use Unturned's **Plugin Key** system instead. Each reviver must bind it once
(`Options → Controls → "Plugin Key 1"`). `RevivePluginKeyIndex` is **0-based** (`0` = "Plugin Key 1"). Note: the server
cannot read a raw F press, so F only works through this Plugin Key binding.

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
  <ReviveDistance>4</ReviveDistance>
  <ReviveInput>CROUCH</ReviveInput>
  <DownedPose>SIT</DownedPose>
  <RevivePluginKeyIndex>0</RevivePluginKeyIndex>
  <InvincibleWhileDowned>false</InvincibleWhileDowned>
  <PauseDrainWhileReviving>true</PauseDrainWhileReviving>
  <MessageKnocked Text="If knocked down, wait for a teammate to revive you" Color="white" />
  <MessageRevived Text="You have been revived" Color="green" />
  <MessageBeingRevived Text="A teammate is reviving you" Color="green" />
  <MessageReviveCancelled Text="Revive cancelled" Color="red" />
  <MessageReviveStarted Text="Reviving... stay crouched and close" Color="yellow" />
  <MessageReviveProgress Text="Reviving... {seconds}s left ({percent}%)" Color="yellow" />
  <MessageDownedHp Text="Bleeding out... HP {hp} ({seconds}s left)" Color="red" />
</KnockdownConfiguration>
```

| Key | Default | Meaning |
|-----|---------|---------|
| `KnockDuration` | 60 | Total downed lifetime in seconds (HP reaches 0 at the end) |
| `ReviveDuration` | 8 | Seconds the revive input must be held |
| `ReviveHealth` | 25 | HP restored on revive (0–100) |
| `KnockHealth` | 100 | Starting HP when downed; drains toward 0 over time (0–100) |
| `InvincibleDuration` | 3 | Grace seconds at start: immune + HP held before draining begins |
| `CrawlSpeed` | 0.25 | Movement multiplier while downed (1.0 = normal) |
| `KnockEffectID` | 61 | Effect asset id on knockdown |
| `ReviveEffectID` | 61 | Effect asset id on revive |
| `ReviveSoundEffectID` | 56 | Sound effect played once/sec while reviving (vanilla "Beep"; 0 = off) |
| `ReviveDistance` | 4 | Max metres between reviver and downed player |
| `ReviveInput` | CROUCH | `CROUCH` = hold crouch near them (no binding); `PLUGINKEY` = hold a bound plugin key |
| `DownedPose` | SIT | Downed pose: `SIT`/`REST`, `CROUCH`, or `PRONE` |
| `RevivePluginKeyIndex` | 0 | 0-based plugin key slot, used only when `ReviveInput = PLUGINKEY` |
| `InvincibleWhileDowned` | false | Behaviour after grace: `true` = stay immune (only HP drain kills); `false` = combat damage can finish them early |
| `PauseDrainWhileReviving` | true | `true` = HP bleed-out + timer pause while a revive is in progress (can't bleed out mid-revive) |
| `MessageReviveProgress` | — | Shown each second while reviving. Placeholders: `{seconds}`, `{total}`, `{percent}` |
| `MessageDownedHp` | — | Shown each second to the downed player while bleeding. Placeholders: `{hp}`, `{seconds}` |

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

---

## Implementation notes & known trade-offs

- **Lethality check ignores armour.** Downing triggers when `damage * times >= currentHealth`. Replicating Unturned's
  exact armour math at the pre-damage hook isn't feasible; worst case a heavily-armoured player is downed slightly early
  rather than killed. This is the standard approach for downed-state plugins.
- **Prone is best-effort.** Stance is client-authoritative, so `checkStance(PRONE)` is re-asserted once per second but may
  not always stick visually. Movement restriction (crawl) and combat lockout are fully enforced server-side.
- **Effect ids must exist** as EFFECT assets on the server, otherwise a warning is logged and the effect is skipped.
- Compiled artifact from the verification build is in `bin\Knockdown.dll` (built with the framework Roslyn compiler);
  prefer rebuilding via Visual Studio for distribution.
