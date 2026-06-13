// =============================================================================
//  DeathBox - the loot box dropped when a DOWNED player combat-logs.
//
//  This is the anti-combat-log half of Knockdown. When a player disconnects
//  while in the downed state we (in the Provider.removePlayer prefix, BEFORE the
//  disconnect save) move their carried loot into a single server-owned storage
//  box at their logout spot and empty their grid so nothing dupes. The box is:
//    * PUBLIC          - anyone can open it immediately (no owner lock).
//    * NON-SALVAGEABLE - owned by the server (owner id 0), can't be picked up.
//    * DEPOSIT-LOCKED  - players take items out but can't put any in.
//    * RESIZED to fit  - the grid grows to exactly hold the dropped loot.
//    * DECAYING        - after DecayMinutes it plays the break effect and dies;
//                        it also vanishes the moment it has been emptied.
//
//  State is IN-MEMORY only; leftover boxes from a previous session are swept on
//  load so nothing survives the daily restart (mirrors the Deadbox plugin).
//
//  Box machinery (spawn / resize-to-fit / deposit-block / grid packer) is ported
//  from the Deadbox + SleepingPlayers plugins so Knockdown stays self-contained
//  (no cross-assembly dependency, no shared Provider.removePlayer hook).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace Knockdown
{
    /// <summary>A spawned death box currently ticking toward decay.</summary>
    internal sealed class ActiveDeathBox
    {
        public uint InstanceID;          // barricade instance id
        public int StorageTransformId;   // InteractableStorage transform id (deposit-block lookup)
        public InteractableStorage Storage; // for the "empty -> vanish" check
        public Vector3 Position;
        public float ExpireTime;         // realtimeSinceStartup deadline
    }

    /// <summary>
    /// Owns every loot box dropped by a downed logout: spawning, resize-to-fit,
    /// per-second decay, and the load-time orphan sweep. Driven from
    /// Knockdown.FixedUpdate (it is a plain class, not a MonoBehaviour).
    /// </summary>
    internal sealed class DeathBoxManager
    {
        private readonly Dictionary<uint, ActiveDeathBox> _boxes = new Dictionary<uint, ActiveDeathBox>();

        // InteractableStorage transform ids of live boxes -> used by the deposit block.
        internal static readonly HashSet<int> BoxStorageTransformIds = new HashSet<int>();

        private float _tickAccum;

        public int ActiveBoxCount { get { return _boxes.Count; } }

        private static KnockdownConfiguration Cfg
        {
            get { return Knockdown.Instance != null ? Knockdown.Instance.Configuration.Instance : null; }
        }

        // ---------------------------------------------------------------------
        //  Spawning
        // ---------------------------------------------------------------------
        /// <summary>
        /// Drops a server-owned box at <paramref name="position"/> holding <paramref name="loot"/>.
        /// Returns true if a box was spawned (false if the asset is bad or nothing fit, in which
        /// case any leftover loot was re-dropped on the ground).
        /// </summary>
        public bool SpawnLootBox(Vector3 position, List<Item> loot)
        {
            KnockdownConfiguration cfg = Cfg;
            if (cfg == null || loot == null) return false;

            ItemBarricadeAsset asset = Assets.find(EAssetType.ITEM, cfg.DownedLogoutBoxAssetId) as ItemBarricadeAsset;
            if (asset == null)
            {
                Logger.LogWarning(string.Format("[Knockdown] DownedLogoutBoxAssetId={0} is not a barricade asset - dropping loot on ground.", cfg.DownedLogoutBoxAssetId));
                RedropItems(loot, position);
                return false;
            }

            Transform t;
            try
            {
                // Use BarricadeManager.getRotation (NOT raw Quaternion.Euler): it folds in the
                // asset's own rotation offset so crates that would spawn upside-down land upright.
                Quaternion rot = BarricadeManager.getRotation(asset, cfg.DownedLogoutBoxRotationX, cfg.DownedLogoutBoxRotationY, cfg.DownedLogoutBoxRotationZ);
                Vector3 spawnPos = position + new Vector3(0f, cfg.DownedLogoutBoxSpawnHeightOffset, 0f);
                // owner=0, group=0 -> server-owned: players can't salvage it.
                t = BarricadeManager.dropNonPlantedBarricade(new Barricade(asset), spawnPos, rot, 0UL, 0UL);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[Knockdown] dropNonPlantedBarricade failed - dropping loot on ground.");
                RedropItems(loot, position);
                return false;
            }
            if (t == null)
            {
                Logger.LogWarning("[Knockdown] barricade drop returned null - dropping loot on ground.");
                RedropItems(loot, position);
                return false;
            }

            InteractableStorage storage = t.GetComponent<InteractableStorage>() ?? t.GetComponentInParent<InteractableStorage>();
            if (storage == null || storage.items == null)
            {
                Logger.LogWarning("[Knockdown] spawned barricade has no InteractableStorage - destroying it, loot to ground.");
                DestroyBarricadeTransform(t);
                RedropItems(loot, position);
                return false;
            }

            int placed = FillStorage(storage, loot, position);
            if (placed == 0)
            {
                // Everything overflowed to the ground (or there was nothing) -> an empty box is pointless.
                DestroyBarricadeTransform(t);
                return false;
            }

            byte x, y; ushort plant, index; BarricadeRegion region; BarricadeDrop drop;
            if (!BarricadeManager.tryGetInfo(t, out x, out y, out plant, out index, out region, out drop))
            {
                Logger.LogWarning("[Knockdown] tryGetInfo failed on fresh death box - it will not be tracked for decay.");
                return true; // box exists in the world, just untracked
            }

            uint instanceId = drop.instanceID;
            int storageTid = storage.transform.GetInstanceID();
            _boxes[instanceId] = new ActiveDeathBox
            {
                InstanceID = instanceId,
                StorageTransformId = storageTid,
                Storage = storage,
                Position = t.position,
                ExpireTime = Time.realtimeSinceStartup + Math.Max(1, cfg.DownedLogoutBoxDecayMinutes) * 60f,
            };
            BoxStorageTransformIds.Add(storageTid);

            Logger.Log(string.Format("[Knockdown] Death box spawned: {0} item(s) stored, decays in {1}m.",
                placed, cfg.DownedLogoutBoxDecayMinutes));
            return true;
        }

        // ---------------------------------------------------------------------
        //  Per-second decay (called from Knockdown.FixedUpdate)
        // ---------------------------------------------------------------------
        public void Tick(float dt)
        {
            if (_boxes.Count == 0) return;
            _tickAccum += dt;
            if (_tickAccum < 1f) return;
            _tickAccum = 0f;

            float now = Time.realtimeSinceStartup;
            List<uint> expired = null;
            foreach (KeyValuePair<uint, ActiveDeathBox> kv in _boxes)
            {
                if (now >= kv.Value.ExpireTime || IsBoxEmpty(kv.Value))
                    (expired ?? (expired = new List<uint>())).Add(kv.Key);
            }
            if (expired == null) return;

            for (int i = 0; i < expired.Count; i++)
            {
                ActiveDeathBox box = _boxes[expired[i]];
                BreakBox(box);
                _boxes.Remove(expired[i]);
                BoxStorageTransformIds.Remove(box.StorageTransformId);
            }
        }

        private static bool IsBoxEmpty(ActiveDeathBox box)
        {
            try
            {
                if (box.Storage == null || box.Storage.items == null) return true;
                return box.Storage.items.getItemCount() == 0;
            }
            catch { return true; }
        }

        // ---------------------------------------------------------------------
        //  Housekeeping
        // ---------------------------------------------------------------------
        public int ClearAll()
        {
            int n = 0;
            List<ActiveDeathBox> snapshot = new List<ActiveDeathBox>(_boxes.Values);
            foreach (ActiveDeathBox box in snapshot)
            {
                BreakBox(box);
                BoxStorageTransformIds.Remove(box.StorageTransformId);
                n++;
            }
            _boxes.Clear();
            return n;
        }

        /// <summary>Destroy leftover boxes (same asset, owner=server) from a previous session.</summary>
        public void SweepOrphans()
        {
            KnockdownConfiguration cfg = Cfg;
            if (cfg == null) return;
            int removed = 0;
            ushort assetId = cfg.DownedLogoutBoxAssetId;
            try
            {
                if (BarricadeManager.regions == null) return; // level not ready
                List<Transform> kill = new List<Transform>();
                foreach (BarricadeRegion region in BarricadeManager.regions)
                {
                    if (region == null || region.drops == null) continue;
                    foreach (BarricadeDrop drop in region.drops)
                    {
                        if (drop == null || drop.asset == null) continue;
                        if (drop.asset.id != assetId) continue;
                        BarricadeData data = drop.GetServersideData();
                        if (data == null || data.owner != 0UL) continue; // only server-owned = our boxes
                        kill.Add(drop.model);
                    }
                }
                for (int i = 0; i < kill.Count; i++)
                {
                    DestroyBarricadeTransform(kill[i]);
                    removed++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[Knockdown] death-box orphan sweep failed");
            }
            if (removed > 0)
                Logger.Log(string.Format("[Knockdown] Swept {0} leftover death box(es) from a previous session.", removed));
        }

        // ---------------------------------------------------------------------
        //  Storage shaping (resize-to-fit, ported from Deadbox/SleepingPlayers)
        // ---------------------------------------------------------------------
        private int FillStorage(InteractableStorage storage, List<Item> items, Vector3 groundPoint)
        {
            KnockdownConfiguration cfg = Cfg;
            Items grid = storage.items;

            // First-Fit Decreasing: biggest items first so guns claim contiguous space.
            items.Sort((a, b) => ItemArea(b).CompareTo(ItemArea(a)));

            byte width = grid.width > 0 ? grid.width : (byte)6;
            if (cfg.DownedLogoutBoxStorageWidth > width) width = cfg.DownedLogoutBoxStorageWidth; // only ever widen
            foreach (Item item in items)
            {
                ItemAsset ia = Assets.find(EAssetType.ITEM, item.id) as ItemAsset;
                if (ia == null) continue;
                byte need = ia.size_x < ia.size_y ? ia.size_x : ia.size_y;
                if (need > width) width = need;
            }

            byte maxH = cfg.DownedLogoutBoxMaxHeight > 0 ? cfg.DownedLogoutBoxMaxHeight : (byte)255;
            GridPacker packer = new GridPacker(width, maxH);
            List<Placement> placements = new List<Placement>(items.Count);
            List<Item> overflow = null;

            foreach (Item item in items)
            {
                ItemAsset a = Assets.find(EAssetType.ITEM, item.id) as ItemAsset;
                byte sx = a != null ? a.size_x : (byte)1;
                byte sy = a != null ? a.size_y : (byte)1;
                Placement p;
                if (packer.TryPlace(sx, sy, out p)) { p.Item = item; placements.Add(p); }
                else (overflow ?? (overflow = new List<Item>())).Add(item);
            }

            byte neededH = (byte)Mathf.Clamp(packer.UsedHeight, 1, maxH);
            byte targetH = neededH > grid.height ? neededH : grid.height;
            TryResize(grid, width, targetH);

            byte aw = grid.width, ah = grid.height;
            GridPacker real = new GridPacker(aw, ah);
            int stored = 0;
            foreach (Placement p in placements)
            {
                ItemAsset a = Assets.find(EAssetType.ITEM, p.Item.id) as ItemAsset;
                byte sx = a != null ? a.size_x : (byte)1;
                byte sy = a != null ? a.size_y : (byte)1;
                Placement np;
                if (real.TryPlace(sx, sy, out np))
                {
                    try { grid.addItem(np.X, np.Y, np.Rotated ? (byte)1 : (byte)0, p.Item); stored++; }
                    catch (Exception ex)
                    {
                        Logger.LogException(ex, "[Knockdown] addItem failed - item to ground");
                        (overflow ?? (overflow = new List<Item>())).Add(p.Item);
                    }
                }
                else (overflow ?? (overflow = new List<Item>())).Add(p.Item);
            }

            if (overflow != null && overflow.Count > 0)
            {
                if (cfg.DownedLogoutBoxOverflowToGround)
                {
                    Logger.Log(string.Format("[Knockdown] {0} item(s) overflowed the death box -> dropped on ground.", overflow.Count));
                    RedropItems(overflow, groundPoint);
                }
                else
                    Logger.LogWarning(string.Format("[Knockdown] {0} item(s) did not fit and OverflowToGround=false -> discarded.", overflow.Count));
            }
            return stored;
        }

        // ---------------------------------------------------------------------
        //  Destruction helpers
        // ---------------------------------------------------------------------
        private static void BreakBox(ActiveDeathBox box)
        {
            try
            {
                ushort fx = Cfg != null ? Cfg.DownedLogoutBoxBreakEffectId : (ushort)0;
                if (fx != 0)
                    EffectManager.sendEffect(fx, 64f, box.Position);
            }
            catch (Exception ex) { Logger.LogException(ex, "[Knockdown] death-box break effect failed"); }
            DestroyBoxByInstance(box.InstanceID);
        }

        private static void DestroyBoxByInstance(uint instanceId)
        {
            try
            {
                if (BarricadeManager.regions == null) return;
                foreach (BarricadeRegion region in BarricadeManager.regions)
                {
                    if (region == null || region.drops == null) continue;
                    for (int i = 0; i < region.drops.Count; i++)
                    {
                        BarricadeDrop drop = region.drops[i];
                        if (drop == null || drop.instanceID != instanceId) continue;
                        DestroyBarricadeTransform(drop.model);
                        return;
                    }
                }
            }
            catch (Exception ex) { Logger.LogException(ex, "[Knockdown] DestroyBoxByInstance failed"); }
        }

        private static void DestroyBarricadeTransform(Transform t)
        {
            if (t == null) return;
            byte x, y; ushort plant, index; BarricadeRegion region; BarricadeDrop drop;
            if (!BarricadeManager.tryGetInfo(t, out x, out y, out plant, out index, out region, out drop)) return;
            BarricadeManager.destroyBarricade(drop, x, y, plant);
        }

        /// <summary>Drops items back on the world (used for overflow / spawn failure).</summary>
        private static void RedropItems(List<Item> items, Vector3 point)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null) continue;
                try { ItemManager.dropItem(item, point, false, true, true); }
                catch (Exception ex) { Logger.LogException(ex, "[Knockdown] re-drop failed"); }
            }
        }

        private static int ItemArea(Item item)
        {
            ItemAsset a = item != null ? Assets.find(EAssetType.ITEM, item.id) as ItemAsset : null;
            if (a == null) return 1;
            return (int)a.size_x * a.size_y;
        }

        private static MethodInfo _resizeMethod;
        private static bool _resizeResolved;

        private static void TryResize(Items grid, byte w, byte h)
        {
            try
            {
                if (!_resizeResolved)
                {
                    _resizeMethod = typeof(Items).GetMethod("resize",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(byte), typeof(byte) }, null);
                    _resizeResolved = true;
                }
                if (_resizeMethod != null)
                    _resizeMethod.Invoke(grid, new object[] { w, h });
            }
            catch { /* keep the asset's native grid if resize is unavailable */ }
        }
    }

    // -------------------------------------------------------------------------
    //  DUPE GUARD + capture trigger. Provider.removePlayer(byte index) is the
    //  single funnel through which a leaving player is torn down; it calls
    //  Player.save() -> PlayerInventory.save() to persist the character. Running
    //  in a PREFIX (before that save) lets Knockdown move the loot into the box
    //  and empty the grid FIRST, so the reconnecting character can never hold a
    //  duplicate of the loot now sitting in the box.
    // -------------------------------------------------------------------------
    [HarmonyPatch(typeof(Provider), "removePlayer")]
    internal static class KnockdownRemovePlayerPatch
    {
        private static void Prefix(byte index)
        {
            Knockdown plugin = Knockdown.Instance;
            if (plugin == null) return;
            try
            {
                if (Provider.clients == null || index >= Provider.clients.Count) return;
                SteamPlayer sp = Provider.clients[index];
                if (sp == null || sp.player == null) return;
                // Ideal hook: runs BEFORE the disconnect save so the grid clear prevents dupes.
                // On builds where this prefix never fires, OnPlayerDisconnected handles it instead
                // (HandleDownedLogout is idempotent via the _downed check).
                plugin.HandleDownedLogout(sp.player, sp.playerID.steamID);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[Knockdown] removePlayer prefix failed");
            }
        }
    }

    // -------------------------------------------------------------------------
    //  Block depositing items INTO a death box (loot-only). Patched manually onto
    //  PlayerInventory.ReceiveDragItem / ReceiveSwapItem. Args read positionally
    //  via __args so it survives parameter renames:
    //    drag : (page_0, x_0, y_0, page_1, x_1, y_1, rot_1)  -> dest page = [3]
    //    swap : (page_0, x_0, y_0, rot_0, page_1, x_1, y_1)  -> dest page = [4]
    // -------------------------------------------------------------------------
    internal static class DeathBoxDepositGuard
    {
        public static bool Prefix(PlayerInventory __instance, object[] __args)
        {
            try
            {
                InteractableStorage open = __instance != null ? __instance.storage : null;
                if (open == null) return true;
                if (!DeathBoxManager.BoxStorageTransformIds.Contains(open.transform.GetInstanceID()))
                    return true; // not a death box -> normal behaviour

                if (__args != null && TargetsStorage(__args))
                    return false; // cancel: no depositing into a death box
            }
            catch { /* on any doubt, let vanilla handle it */ }
            return true;
        }

        private static bool TargetsStorage(object[] args)
        {
            if (args.Length > 3) { try { if (Convert.ToByte(args[3]) == PlayerInventory.STORAGE) return true; } catch { } }
            if (args.Length > 4) { try { if (Convert.ToByte(args[4]) == PlayerInventory.STORAGE) return true; } catch { } }
            return false;
        }
    }

    // -------------------------------------------------------------------------
    //  Minimal 2D first-fit bin packer (rotation aware), used to lay loot into
    //  the resized box grid. Ported from Deadbox / OneClickSort.
    // -------------------------------------------------------------------------
    internal struct Placement
    {
        public byte X;
        public byte Y;
        public bool Rotated;
        public Item Item;
    }

    internal sealed class GridPacker
    {
        private readonly bool[,] _used;
        private readonly byte _w;
        private readonly byte _h;
        public int UsedHeight { get; private set; }

        public GridPacker(byte width, byte height)
        {
            _w = width < 1 ? (byte)1 : width;
            _h = height < 1 ? (byte)1 : height;
            _used = new bool[_w, _h];
            UsedHeight = 0;
        }

        public bool TryPlace(byte sx, byte sy, out Placement placement)
        {
            if (sx < 1) sx = 1;
            if (sy < 1) sy = 1;
            if (TryPlaceOriented(sx, sy, false, out placement)) return true;
            if (sx != sy && TryPlaceOriented(sy, sx, true, out placement)) return true;
            placement = default(Placement);
            return false;
        }

        private bool TryPlaceOriented(byte w, byte h, bool rotated, out Placement placement)
        {
            placement = default(Placement);
            if (w > _w || h > _h) return false;

            for (int y = 0; y <= _h - h; y++)
            {
                for (int x = 0; x <= _w - w; x++)
                {
                    if (Fits(x, y, w, h))
                    {
                        Occupy(x, y, w, h);
                        placement = new Placement { X = (byte)x, Y = (byte)y, Rotated = rotated };
                        if (y + h > UsedHeight) UsedHeight = y + h;
                        return true;
                    }
                }
            }
            return false;
        }

        private bool Fits(int x, int y, int w, int h)
        {
            for (int dy = 0; dy < h; dy++)
                for (int dx = 0; dx < w; dx++)
                    if (_used[x + dx, y + dy]) return false;
            return true;
        }

        private void Occupy(int x, int y, int w, int h)
        {
            for (int dy = 0; dy < h; dy++)
                for (int dx = 0; dx < w; dx++)
                    _used[x + dx, y + dy] = true;
        }
    }
}
