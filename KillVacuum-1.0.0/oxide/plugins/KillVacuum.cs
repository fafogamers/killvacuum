// KillVacuum.cs — Oxide + Carbon
// FAFO Vibin of FAFO Gamers
//
// Vacuums NPC, zombie horde, and animal corpses into a paged buffer backpack.
// Every Rust item category has its own page. Anything that does not fit a
// category, or whose category page is turned off, goes to Misc.
// Animals are skinned. NPC and zombie bodies are not. Fish are gutted. Food kept
// in the buffer is refrigerated so it does not spoil. The corpse and its body bag
// are removed after the loot is taken.
//
// Install
//   oxide/plugins/KillVacuum.cs     or     carbon/plugins/KillVacuum.cs
//   oxide.grant group default killvacuum.use
//   oxide.grant group admin killvacuum.admin
//
// Player commands
//   /vacuum                         toggle vacuuming on or off
//   /vacuum on|off
//   /vacuum open [page]             open a buffer page
//   /vacuum move [rail|button|done|reset|left|right]
//   /vacuum clear <page|all> confirm
//   /vacuum last                    what the last kill did
//
// Config
//   "Max Vacuum Distance (0 = unlimited)"   how close you must be to the body
//                                           before loot is pulled. The kill itself
//                                           can be from any distance. Default 6.
//   "Zombie Corpse Loot Only"               true = the main corpse container only.
//                                           Belt, worn costumes, and attire in that
//                                           container are not taken. Zombies are not skinned.
//   "Button Image URL"                      https link to a png or jpg. Leave
//                                           blank for the plain VAC button.
//                                           The image fills the button. ON and
//                                           the stack count sit under it.
//   "Clear Buffer On Map Wipe"              true = a new map save empties every
//                                           Kill Vacuum buffer. Player UI
//                                           positions are cleared with the loot.
//                                           false = buffers carry over the wipe.
//                                           (disabled categories fall through to Misc)
//
// The hotbar button opens the buffer. Vacuuming can be off and the buffer still opens,
// so stored loot is never trapped. Each page is a real large wooden box owned by the
// player. Sort Button can sort that box. Stack Size Controller is honored because new
// stacks call Item.MaxStackable(), which SSC replaces through OnMaxStackable, and the
// boxes leave maxStackSize at 0 so they do not impose a second cap. Filing
// merges into an existing stack up to the larger of Item.MaxStackable() and
// info.stackable before it takes an empty slot.
//
// Sort safety (do not "fix" this by rebuilding the box when items move):
//   Sort Button pulls every item out with RemoveFromContainer and puts it back.
//   Anything that does not fit is given to the player. It does not delete items.
//   This plugin does not hook CanMoveItem, CanAcceptItem, CanStackItem,
//   OnItemStacked, or OnItemRemove. Item-move hooks only mark the save dirty.
//   Saves are deferred, so a save cannot run in the middle of a sort and write
//   an empty box. New loot is filed once, at vacuum time, and then left alone.
//   A full page drops the overflow on the ground and says so in chat. It is not deleted.

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("KillVacuum", "FAFO Vibin of FAFO Gamers", "1.0.0")]
    [Description("Vacuums NPC, zombie, and animal corpses into category pages plus a Misc catch-all. Skins animals, guts fish, and removes the body.")]
    public class KillVacuum : RustPlugin
    {
        private const string PermUse = "killvacuum.use";
        private const string PermAdmin = "killvacuum.admin";
        private const string BoxPrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";
        private const ulong MarkerSkin = 8712459012UL;
        private const string UiButton = "KillVacuum.Button";
        private const string UiCount = "KillVacuum.Count";
        private const string UiRail = "KillVacuum.Rail";
        private const string UiPad = "KillVacuum.Pad";
        private const string UiGrid = "KillVacuum.Grid";
        private const string UiFull = "KillVacuum.Full";
        private const float SaveDelay = 3f;
        private const float CorpseMatchRadius = 4.5f;
        private const float ButtonW = 60f;
        private const float ButtonH = 60f;
        private const float RailW = 296f;
        private const float RailH = 372f;
        private const int GridCols = 8;
        private const int GridRows = 5;
        private const int ConfigVersion = 5;

        private static readonly string[] LegacyPages = { "npc", "zed", "animal" };
        private static readonly string[] CategoryPages =
        {
            "weapons", "construction", "items", "resources", "attire", "tools",
            "meds", "food", "ammo", "traps", "misc", "components", "electrical", "fun"
        };
        private static readonly string[] PageOrder =
        {
            "weapons", "construction", "items", "resources", "attire", "tools",
            "meds", "food", "ammo", "traps", "misc", "components", "electrical", "fun"
        };

        private PluginConfig _config;
        private Dictionary<ulong, PlayerStore> _store = new Dictionary<ulong, PlayerStore>();
        private readonly Dictionary<ulong, Dictionary<string, StorageContainer>> _boxes = new Dictionary<ulong, Dictionary<string, StorageContainer>>();
        private readonly Dictionary<ulong, ulong> _boxOwner = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, string> _openPage = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _moveTarget = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, float> _nextUiMove = new Dictionary<ulong, float>();
        private readonly HashSet<ulong> _uiTrailing = new HashSet<ulong>();
        private readonly Dictionary<ulong, string> _lastNote = new Dictionary<ulong, string>();
        private readonly List<PendingKill> _pending = new List<PendingKill>();
        private readonly HashSet<int> _scheduled = new HashSet<int>();
        private readonly List<BaseEntity> _scan = new List<BaseEntity>();
        private readonly HashSet<ulong> _dirty = new HashSet<ulong>();
        private readonly Dictionary<ulong, Vector3> _feet = new Dictionary<ulong, Vector3>();
        private readonly HashSet<ulong> _uiQueued = new HashSet<ulong>();
        private readonly HashSet<ulong> _buttonDrawn = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> _fullWarn = new Dictionary<ulong, int>();
        private bool _saveQueued;
        private bool _unloading;
        private bool _wipeThisBoot;

        #region Config

        private class PluginConfig
        {
            [JsonProperty("Config Version")]
            public int Version;

            [JsonProperty("Vacuum Delay Seconds")]
            public float VacuumDelay = 1f;

            [JsonProperty("Max Vacuum Distance (0 = unlimited)")]
            public float MaxDistance = 6f;

            [JsonProperty("Slots Per Page")]
            public int SlotsPerPage = 48;

            [JsonProperty("Harvest Animals")]
            public bool HarvestAnimals = true;

            [JsonProperty("Harvest All Bodies")]
            public bool HarvestBodies = true;

            [JsonProperty("Refrigerate Food")]
            public bool Refrigerate = true;

            [JsonProperty("Harvest Multiplier")]
            public float HarvestMultiplier = 1f;

            [JsonProperty("Gut Fish In Corpses")]
            public bool GutFish = true;

            [JsonProperty("Gut Fish On Catch")]
            public bool GutFishOnCatch = true;

            [JsonProperty("Gutted Fish Shortname")]
            public string GuttedFishShortname = "fish.raw";

            [JsonProperty("Default Fish Yield")]
            public int DefaultFishYield = 1;

            [JsonProperty("Fish Yield By Shortname")]
            public Dictionary<string, int> FishYield = new Dictionary<string, int>();

            [JsonProperty("Fish Ignore Shortnames")]
            public List<string> FishIgnore = new List<string>();

            [JsonProperty("Remove Corpse After Vacuum")]
            public bool RemoveCorpse = true;

            [JsonProperty("Credit Owned Turrets And Traps")]
            public bool CreditOwnedTraps = false;

            [JsonProperty("Vacuum NPC Backpacks")]
            public bool VacuumNpcBackpacks = true;

            [JsonProperty("Zombie Corpse Loot Only")]
            public bool ZombieCorpseLootOnly = true;

            [JsonProperty("Drop Buffer On Death")]
            public bool DropOnDeath = false;

            [JsonProperty("Chat Notify On Vacuum")]
            public bool Notify = false;

            [JsonProperty("Warn When A Page Is Full")]
            public bool WarnWhenFull = true;

            [JsonProperty("Clear Buffer On Map Wipe")]
            public bool ClearOnWipe = true;

            [JsonProperty("Admins Bypass Use Permission")]
            public bool AdminsBypass = true;

            [JsonProperty("Debug")]
            public bool Debug = false;

            [JsonProperty("Zombie Keywords")]
            public List<string> ZombieKeywords = new List<string>();

            [JsonProperty("Animal Keywords")]
            public List<string> AnimalKeywords = new List<string>();

            [JsonProperty("Keep On Source Shortnames")]
            public List<string> KeepOnSource = new List<string>();

            [JsonProperty("Extra Food Shortnames")]
            public List<string> FoodItems = new List<string>();

            [JsonProperty("Extra Med Shortnames")]
            public List<string> MedItems = new List<string>();

            [JsonProperty("Extra Ammo Shortnames")]
            public List<string> AmmoItems = new List<string>();

            [JsonProperty("Extra Weapon Shortnames")]
            public List<string> WeaponItems = new List<string>();

            [JsonProperty("Enable Food Page")]
            public bool EnableFood = true;

            [JsonProperty("Enable Meds Page")]
            public bool EnableMeds = true;

            [JsonProperty("Enable Ammo Page")]
            public bool EnableAmmo = true;

            [JsonProperty("Enable Weapons Page")]
            public bool EnableWeapons = true;

            [JsonProperty("Enabled Category Pages")]
            public Dictionary<string, bool> EnabledCategories = new Dictionary<string, bool>();

            [JsonProperty("Button Offset Min")]
            public string ButtonMin = "254 18";

            [JsonProperty("Button Offset Max")]
            public string ButtonMax = "314 78";

            [JsonProperty("Button Image URL")]
            public string ButtonImageUrl = "";

            [JsonProperty("Page Rail Offset Min")]
            public string RailMin = "12 -240";

            [JsonProperty("Page Rail Offset Max")]
            public string RailMax = "308 240";

            [JsonProperty("Button Color On")]
            public string ColorOn = "0.55 0.28 0.10 0.95";

            [JsonProperty("Button Color Off")]
            public string ColorOff = "0.16 0.17 0.13 0.92";

            [JsonProperty("UI Move Step")]
            public float UiMoveStep = 16f;

            [JsonProperty("UI Move Sensitivity")]
            public float UiMoveSensitivity = 1.15f;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            FillConfigDefaults(_config);
            _config.Version = ConfigVersion;
            SaveConfig();
        }

        private void LoadConfigValues()
        {
            try
            {
                _config = Config.ReadObject<PluginConfig>();
            }
            catch
            {
                _config = null;
            }
            if (_config == null)
            {
                LoadDefaultConfig();
                return;
            }
            FillConfigDefaults(_config);
            MigrateConfig(_config);
            _config.SlotsPerPage = Mathf.Clamp(_config.SlotsPerPage, 6, 48);
            if (_config.VacuumDelay < 0.05f) _config.VacuumDelay = 0.05f;
            if (_config.HarvestMultiplier < 0f) _config.HarvestMultiplier = 0f;
            if (_config.MaxDistance < 0f) _config.MaxDistance = 0f;
            if (_config.UiMoveStep < 1f) _config.UiMoveStep = 1f;
            if (_config.UiMoveSensitivity < 0.05f) _config.UiMoveSensitivity = 0.05f;
            if (string.IsNullOrEmpty(_config.GuttedFishShortname)) _config.GuttedFishShortname = "fish.raw";
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void MigrateConfig(PluginConfig config)
        {
            if (config.Version >= ConfigVersion) return;
            if (config.EnabledCategories == null) config.EnabledCategories = new Dictionary<string, bool>();
            config.EnabledCategories["food"] = config.EnableFood;
            config.EnabledCategories["meds"] = config.EnableMeds;
            config.EnabledCategories["ammo"] = config.EnableAmmo;
            config.EnabledCategories["weapons"] = config.EnableWeapons;
            if (config.Version < 4 && (Mathf.Approximately(config.MaxDistance, 50f) || Mathf.Approximately(config.MaxDistance, 3f)))
            {
                config.MaxDistance = 6f;
                Puts("Max Vacuum Distance is now 6 meters. Change \"Max Vacuum Distance (0 = unlimited)\" if you want a different range.");
            }
            EnsureKeyword(config.ZombieKeywords, "scarecrow");
            EnsureKeyword(config.ZombieKeywords, "murderer");
            EnsureKeyword(config.ZombieKeywords, "frankenstein");
            EnsureKeyword(config.AnimalKeywords, "polarbear");
            EnsureKeyword(config.AnimalKeywords, "boar");
            EnsureKeyword(config.AnimalKeywords, "crocodile");
            EnsureKeyword(config.AnimalKeywords, "panther");
            EnsureKeyword(config.AnimalKeywords, "tiger");
            EnsureKeyword(config.AnimalKeywords, "snake");
            EnsureKeyword(config.AnimalKeywords, "shark");
            if (config.RailMin == "12 -230" && config.RailMax == "176 230")
            {
                config.RailMin = "12 -240";
                config.RailMax = "308 240";
            }
            if (config.RailMin == "12 -240" || string.IsNullOrEmpty(config.RailMin)) config.RailMin = "12 -186";
            if (config.RailMax == "308 240" || string.IsNullOrEmpty(config.RailMax)) config.RailMax = "308 186";
            config.Version = ConfigVersion;
        }

        private static void FillConfigDefaults(PluginConfig config)
        {
            if (config.ZombieKeywords == null) config.ZombieKeywords = new List<string>();
            if (config.ZombieKeywords.Count == 0)
            {
                config.ZombieKeywords.Add("zombie");
                config.ZombieKeywords.Add("zed");
                config.ZombieKeywords.Add("zombienpc");
                config.ZombieKeywords.Add("zombiehorde");
                config.ZombieKeywords.Add("zombie_horde");
                config.ZombieKeywords.Add("hzombie");
                config.ZombieKeywords.Add("scarecrow");
                config.ZombieKeywords.Add("murderer");
                config.ZombieKeywords.Add("frankenstein");
            }
            if (config.AnimalKeywords == null) config.AnimalKeywords = new List<string>();
            if (config.AnimalKeywords.Count == 0)
            {
                config.AnimalKeywords.Add("bear");
                config.AnimalKeywords.Add("polarbear");
                config.AnimalKeywords.Add("boar");
                config.AnimalKeywords.Add("wolf");
                config.AnimalKeywords.Add("stag");
                config.AnimalKeywords.Add("chicken");
                config.AnimalKeywords.Add("crocodile");
                config.AnimalKeywords.Add("panther");
                config.AnimalKeywords.Add("tiger");
                config.AnimalKeywords.Add("shark");
                config.AnimalKeywords.Add("snake");
                config.AnimalKeywords.Add("deer");
                config.AnimalKeywords.Add("horse");
            }
            if (config.FishYield == null) config.FishYield = new Dictionary<string, int>();
            if (config.FishYield.Count == 0)
            {
                config.FishYield["fish.anchovy"] = 1;
                config.FishYield["fish.sardine"] = 1;
                config.FishYield["fish.minnows"] = 1;
                config.FishYield["fish.herring"] = 2;
                config.FishYield["fish.troutsmall"] = 4;
                config.FishYield["fish.yellowperch"] = 5;
                config.FishYield["fish.salmon"] = 18;
                config.FishYield["fish.catfish"] = 18;
                config.FishYield["fish.orangeroughy"] = 18;
                config.FishYield["fish.smallshark"] = 20;
            }
            if (config.FishIgnore == null) config.FishIgnore = new List<string>();
            if (config.KeepOnSource == null) config.KeepOnSource = new List<string>();
            if (config.FoodItems == null) config.FoodItems = new List<string>();
            if (config.MedItems == null) config.MedItems = new List<string>();
            if (config.AmmoItems == null) config.AmmoItems = new List<string>();
            if (config.WeaponItems == null) config.WeaponItems = new List<string>();
            if (config.EnabledCategories == null) config.EnabledCategories = new Dictionary<string, bool>();
            for (int i = 0; i < CategoryPages.Length; i++)
            {
                if (!config.EnabledCategories.ContainsKey(CategoryPages[i]))
                    config.EnabledCategories[CategoryPages[i]] = true;
            }
            if (string.IsNullOrEmpty(config.ButtonMin)) config.ButtonMin = "254 18";
            if (string.IsNullOrEmpty(config.ButtonMax)) config.ButtonMax = "314 78";
            if (string.IsNullOrEmpty(config.RailMin)) config.RailMin = "12 -186";
            if (string.IsNullOrEmpty(config.RailMax)) config.RailMax = "308 186";
            if (string.IsNullOrEmpty(config.ColorOn)) config.ColorOn = "0.55 0.28 0.10 0.95";
            if (string.IsNullOrEmpty(config.ColorOff)) config.ColorOff = "0.16 0.17 0.13 0.92";
        }

        private static void EnsureKeyword(List<string> list, string word)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], word, StringComparison.OrdinalIgnoreCase)) return;
            }
            list.Add(word);
        }

        #endregion

        #region Data

        private class PlayerStore
        {
            public bool Enabled = true;
            public string LastPage = "misc";
            public float RailX;
            public float RailY;
            public float ButtonX;
            public float ButtonY;
            public bool RailFree;
            public float RailAX;
            public float RailAY = 0.5f;
            public bool ButtonFree;
            public float ButtonAX = 0.5f;
            public float ButtonAY;
            public Dictionary<string, List<StoredItem>> Pages = new Dictionary<string, List<StoredItem>>();
        }

        private class StoredItem
        {
            public int ItemId;
            public int Amount;
            public ulong Skin;
            public float Condition;
            public float MaxCondition;
            public int Position = -1;
            public int Flags;
            public string Name;
            public string Text;
            public int BlueprintTarget;
            public int DataInt;
            public float DataFloat;
            public bool HasInstance;
            public int Ammo = -1;
            public string AmmoType;
            public List<StoredItem> Contents;
        }

        private class PendingKill
        {
            public ulong Killer;
            public Vector3 Position;
            public float Time;
            public string Source;
            public ulong NpcId;
            public NetworkableId NetId;
            public bool Used;
            public bool SweepQueued;
            public bool ApproachWatch;
            public bool RangeNoted;
            public int Scans;
            public string Label;
        }

        private void LoadData()
        {
            try
            {
                _store = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerStore>>(Name);
            }
            catch (Exception ex)
            {
                PrintWarning("KillVacuum data could not be read, starting fresh. " + ex.Message);
                _store = new Dictionary<ulong, PlayerStore>();
            }
            if (_store == null) _store = new Dictionary<ulong, PlayerStore>();
        }

        private void SaveData()
        {
            if (_store == null || _unloading && _store.Count == 0) return;
            foreach (var id in _boxes.Keys)
                CaptureRuntime(id);
            Interface.Oxide.DataFileSystem.WriteObject(Name, _store);
            _dirty.Clear();
            _saveQueued = false;
        }

        private void MarkDirty(ulong playerId)
        {
            _dirty.Add(playerId);
            if (_saveQueued || _unloading) return;
            _saveQueued = true;
            timer.Once(SaveDelay, () =>
            {
                _saveQueued = false;
                if (_unloading) return;
                SaveData();
            });
        }

        private PlayerStore GetStore(ulong playerId)
        {
            PlayerStore store;
            if (!_store.TryGetValue(playerId, out store) || store == null)
            {
                store = new PlayerStore();
                _store[playerId] = store;
            }
            if (store.Pages == null) store.Pages = new Dictionary<string, List<StoredItem>>();
            FoldLegacy(store);
            for (int i = 0; i < PageOrder.Length; i++)
            {
                List<StoredItem> page;
                if (!store.Pages.TryGetValue(PageOrder[i], out page) || page == null)
                    store.Pages[PageOrder[i]] = new List<StoredItem>();
            }
            if (string.IsNullOrEmpty(store.LastPage) || !IsPage(store.LastPage)) store.LastPage = "misc";
            return store;
        }

        private static void FoldLegacy(PlayerStore store)
        {
            if (store?.Pages == null) return;
            List<StoredItem> misc;
            if (!store.Pages.TryGetValue("misc", out misc) || misc == null)
            {
                misc = new List<StoredItem>();
                store.Pages["misc"] = misc;
            }
            for (int i = 0; i < LegacyPages.Length; i++)
            {
                List<StoredItem> old;
                if (!store.Pages.TryGetValue(LegacyPages[i], out old) || old == null)
                {
                    store.Pages.Remove(LegacyPages[i]);
                    continue;
                }
                for (int n = 0; n < old.Count; n++)
                {
                    if (old[n] != null) misc.Add(old[n]);
                }
                store.Pages.Remove(LegacyPages[i]);
            }
            if (store.LastPage == "npc" || store.LastPage == "zed" || store.LastPage == "animal")
                store.LastPage = "misc";
        }

        #endregion

        #region Hooks

        private void Init()
        {
            LoadConfigValues();
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermAdmin, this);
        }

        private void OnServerInitialized()
        {
            LoadData();
            if (_wipeThisBoot && _config.ClearOnWipe)
            {
                _store = new Dictionary<ulong, PlayerStore>();
                SaveData();
                Puts("Map wipe. Kill Vacuum buffers cleared.");
            }
            SweepOrphans();
            foreach (var player in BasePlayer.activePlayerList)
                HandleConnect(player);
        }

        private void OnNewSave(string filename)
        {
            if (_config != null && _config.ClearOnWipe)
                _wipeThisBoot = true;
        }

        private void Unload()
        {
            _unloading = true;
            SaveData();
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyUi(player);
                if (IsLootingVacuum(player)) player.EndLooting();
            }
            DestroyAllBoxes();
        }

        private void OnServerSave() => SaveData();

        private void OnPlayerConnected(BasePlayer player) => HandleConnect(player);

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            ulong id = Id(player);
            CaptureRuntime(id);
            if (IsLootingVacuum(player)) player.EndLooting();
            DestroyUi(player);
            _moveTarget.Remove(id);
            DestroyBoxes(id);
            SaveData();
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(1f, () =>
            {
                if (player != null && player.IsConnected) DrawButton(player);
            });
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null) return;
            timer.Once(1f, () =>
            {
                if (player != null && player.IsConnected) DrawButton(player);
            });
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || player.IsNpc || !_config.DropOnDeath) return;
            ulong id = Id(player);
            if (!_boxes.ContainsKey(id)) return;
            Vector3 pos = player.transform.position;
            for (int p = 0; p < PageOrder.Length; p++)
            {
                StorageContainer box;
                if (!TryGetBox(id, PageOrder[p], out box) || box?.inventory?.itemList == null) continue;
                var copy = CopyItems(box.inventory);
                for (int i = 0; i < copy.Count; i++)
                {
                    var item = copy[i];
                    if (item == null) continue;
                    item.Drop(pos + Vector3.up, Vector3.up);
                }
            }
            CaptureRuntime(id);
            SaveData();
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (!IsVacuumBox(entity)) return null;
            if (info?.damageTypes != null) info.damageTypes.ScaleAll(0f);
            return true;
        }

        private object CanPickupEntity(BasePlayer player, BaseEntity entity)
        {
            if (IsVacuumBox(entity)) return false;
            return null;
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            if (!IsVacuumBox(container)) return null;
            if (container.net == null) return false;
            ulong owner;
            if (!_boxOwner.TryGetValue(container.net.ID.Value, out owner)) return false;
            if (player == null || Id(player) != owner) return false;
            return null;
        }

        // Activity only. Never move, rebuild, or delete here — Sort Button is mid-pass.
        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            NoteActivity(container);
            if (item == null || !IsVacuumContainer(container)) return;
            Chill(container);
            QueueCount(container);
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            NoteActivity(container);
            if (item == null || !IsVacuumContainer(container)) return;
            QueueCount(container);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (_unloading || entity == null || entity.IsDestroyed) return;
            if (entity is LootableCorpse || entity is BaseCorpse) return;
            if (IsVacuumBox(entity)) return;

            ulong killer = ResolveKillerId(entity, info);
            if (killer == 0)
            {
                if (_config.Debug) Puts("No killer for " + SafeName(entity) + " (" + entity.GetType().Name + ")");
                return;
            }

            string source = ClassifySource(entity);
            if (source == null)
            {
                if (_config.Debug) Puts("Ignored " + SafeName(entity) + " type " + entity.GetType().Name);
                return;
            }

            var npc = entity as BasePlayer;
            var job = new PendingKill
            {
                Killer = killer,
                Position = entity.transform.position,
                Time = Time.realtimeSinceStartup,
                Source = source,
                NpcId = npc != null ? Id(npc) : 0UL,
                NetId = entity.net != null ? entity.net.ID : default(NetworkableId),
                Label = SafeName(entity)
            };
            _pending.Add(job);
            Note(killer, "Tracked " + source + " " + job.Label + ". Loot waits until you are within " + RangeLabel() + " of the body.");
            if (_config.Debug)
                Puts("Tracked " + source + " " + job.Label + " id " + job.NpcId + " killer " + killer);

            if (!(source == "zed" && _config.ZombieCorpseLootOnly))
                ScheduleScan(job, 0.25f);
            ScheduleScan(job, Mathf.Max(1.25f, _config.VacuumDelay));
            ScheduleScan(job, Mathf.Max(1.25f, _config.VacuumDelay + 1.25f));
            if (source == "zed" && _config.ZombieCorpseLootOnly)
                ScheduleScan(job, Mathf.Max(3.5f, _config.VacuumDelay + 2.5f));
        }

        private void OnEntitySpawned(BaseNetworkable net)
        {
            var entity = net as BaseEntity;
            if (entity == null || entity.IsDestroyed) return;
            if (entity is DroppedItemContainer)
            {
                var backpack = entity as DroppedItemContainer;
                if (!_config.VacuumNpcBackpacks || backpack == null) return;
                var backpackJob = MatchBackpack(backpack);
                if (backpackJob == null) backpackJob = MatchSpawn(entity);
                if (backpackJob == null || ZombieLootOnly(backpackJob)) return;
                timer.Once(Mathf.Max(0.15f, _config.VacuumDelay), () =>
                {
                    if (_unloading || backpack == null || backpack.IsDestroyed) return;
                    int instance = backpack.GetInstanceID();
                    if (!_scheduled.Add(instance)) return;
                    if (!ExecuteBackpack(backpack, backpackJob))
                    {
                        _scheduled.Remove(instance);
                        if (!backpackJob.Used && !InVacuumRange(backpackJob.Killer, backpack.transform.position))
                            HoldForRange(backpackJob, backpack);
                    }
                    else
                        MarkClaimed(backpackJob);
                });
                return;
            }
            if (!CouldBeLoot(entity)) return;
            var job = MatchSpawn(entity);
            if (job == null) return;
            float wait = _config.VacuumDelay;
            if (ZombieLootOnly(job)) wait = Mathf.Max(wait, 1.25f);
            timer.Once(Mathf.Max(0.15f, wait), () =>
            {
                if (_unloading || entity == null || entity.IsDestroyed) return;
                TryVacuumEntity(entity, job);
            });
        }

        private void OnPlayerCorpseSpawned(BasePlayer player, PlayerCorpse corpse)
        {
            if (corpse == null) return;
            OnEntitySpawned(corpse);
        }

        private void OnFishCatch(Item item, BaseFishingRod rod, BasePlayer player)
        {
            if (!_config.GutFishOnCatch || item == null || player == null) return;
            ulong id = Id(player);
            if (!Allowed(player) || !IsEnabled(id)) return;
            NextTick(() =>
            {
                if (item == null || item.amount <= 0 || item.info == null) return;
                if (!IsWholeFish(item.info.shortname)) return;
                if (IsVacuumContainer(item.parent)) return;
                GutCaughtFish(player, id, item);
            });
        }

        private void OnLootEntityEnd(BasePlayer player, BaseCombatEntity entity)
        {
            if (player == null) return;
            if (entity != null && IsVacuumBox(entity))
            {
                var box = entity as StorageContainer;
                if (box != null)
                {
                    box.SetFlag(BaseEntity.Flags.Open, false);
                    box.limitNetworking = true;
                }
                if (!IsMoving(player)) CuiHelper.DestroyUi(player, UiRail);
                MarkDirty(Id(player));
                ulong owner = Id(player);
                timer.Once(0.1f, () =>
                {
                    var still = FindAny(owner);
                    if (still == null || !still.IsConnected) return;
                    StorageContainer open = entity as StorageContainer;
                    if (open?.inventory != null) DropEmpty(open.inventory);
                    RefreshCount(still);
                });
            }
        }

        #endregion

        #region Vacuum

        private void ScheduleScan(PendingKill job, float delay)
        {
            timer.Once(delay, () => ScanPending(job));
        }

        private void ScanPending(PendingKill job)
        {
            if (_unloading || job == null || job.Used) return;
            job.Scans += 1;
            BaseEntity best = null;
            float bestDist = CorpseMatchRadius;

            _scan.Clear();
            Vis.Entities(job.Position, CorpseMatchRadius, _scan, -1, QueryTriggerInteraction.Collide);
            for (int i = 0; i < _scan.Count; i++)
            {
                var ent = _scan[i];
                if (ent == null || ent.IsDestroyed || !IsLootCandidate(ent, job)) continue;
                if (ent is BasePlayer) continue;
                float dist = Vector3.Distance(job.Position, ent.transform.position);
                bool corpse = ent is LootableCorpse || ent is BaseCorpse;
                bool bestIsCorpse = best is LootableCorpse || best is BaseCorpse;
                if (best != null && bestIsCorpse && !corpse) continue;
                if (corpse && !bestIsCorpse)
                {
                    bestDist = dist;
                    best = ent;
                    continue;
                }
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = ent;
                }
            }

            if (best == null && job.NetId.Value != 0)
            {
                var self = BaseNetworkable.serverEntities.Find(job.NetId) as BaseEntity;
                if (self != null && !self.IsDestroyed && !(self is BasePlayer) && IsLootCandidate(self, job))
                    best = self;
            }

            if (best == null)
            {
                if (job.Scans >= 3)
                    Note(job.Killer, "No corpse found for " + job.Source + " " + job.Label + ".");
                if (_config.Debug && job.Scans >= 3)
                    Puts("No corpse for " + job.Source + " " + job.Label + " killer " + job.Killer);
                return;
            }
            TryVacuumEntity(best, job);
        }

        private void TryVacuumEntity(BaseEntity entity, PendingKill job)
        {
            if (job == null || entity == null || entity.IsDestroyed) return;
            if (!InVacuumRange(job.Killer, entity.transform.position))
            {
                HoldForRange(job, entity);
                return;
            }
            if (!LootReady(entity)) return;
            int instance = entity.GetInstanceID();
            if (!_scheduled.Add(instance)) return;

            bool ran;
            var corpse = entity as LootableCorpse;
            if (corpse != null) ran = ExecuteVacuum(corpse, job);
            else
            {
                var drop = entity as DroppedItemContainer;
                if (drop != null) ran = ExecuteBackpack(drop, job);
                else ran = ExecuteLoose(entity, job);
            }

            if (!ran)
            {
                _scheduled.Remove(instance);
                return;
            }
            MarkClaimed(job);
        }

        private void MarkClaimed(PendingKill job)
        {
            if (job == null) return;
            job.Used = true;
            if (job.SweepQueued || _unloading) return;
            job.SweepQueued = true;
            timer.Once(0.75f, () => SweepRemains(job, false));
            timer.Once(3.5f, () => SweepRemains(job, true));
        }

        private void SweepRemains(PendingKill job, bool finalPass)
        {
            if (_unloading || job == null) return;
            if (!finalPass) job.SweepQueued = false;
            _scan.Clear();
            Vis.Entities(job.Position, 8f, _scan, -1, QueryTriggerInteraction.Collide);
            for (int i = 0; i < _scan.Count; i++)
            {
                var ent = _scan[i];
                if (ent == null || ent.IsDestroyed || IsVacuumBox(ent)) continue;
                var deadNpc = ent as BasePlayer;
                if (deadNpc != null)
                {
                    if (!deadNpc.IsDead() || IsRealPlayer(deadNpc)) continue;
                    if (job.NpcId != 0 && Id(deadNpc) != job.NpcId) continue;
                    if (ShouldHarvest(job, string.IsNullOrEmpty(job.Source) ? "npc" : job.Source) && HasHarvestable(deadNpc))
                    {
                        var session = new VacuumSession();
                        Harvest(deadNpc, job.Killer, string.IsNullOrEmpty(job.Source) ? "npc" : job.Source, session);
                        if (session.Moved > 0 || session.Dropped > 0)
                            FinishVacuum(job.Killer, job.Source, session, SafeName(deadNpc));
                    }
                    ZeroDispenser(deadNpc);
                    deadNpc.Kill();
                    continue;
                }
                if (!IsRemnant(ent, job)) continue;
                if (!OwnsRemnant(ent, job) && Vector3.Distance(job.Position, ent.transform.position) > 2.5f) continue;
                if (!EntityLootEmpty(ent) || HasHarvestable(ent))
                    TryVacuumEntity(ent, job);
                EraseRemains(ent);
            }
        }

        private bool ZombieLootOnly(PendingKill job)
        {
            return _config.ZombieCorpseLootOnly && job != null && job.Source == "zed";
        }

        private bool IsRemnant(BaseEntity ent, PendingKill job)
        {
            if (ent == null || ent.IsDestroyed || ent is BasePlayer || IsVacuumBox(ent) || job == null) return false;
            if (ZombieLootOnly(job) && (ent is DroppedItemContainer || ent is BasePlayer)) return false;
            if (IsLootCandidate(ent, job)) return true;
            if (job.Source == "animal") return false;
            string blob = Blob(ent);
            if (blob.IndexOf("bodybag", StringComparison.Ordinal) < 0 && blob.IndexOf("body_bag", StringComparison.Ordinal) < 0 && blob.IndexOf("item_drop_backpack", StringComparison.Ordinal) < 0)
                return false;
            var drop = ent as DroppedItemContainer;
            if (drop != null && IsSteamId(drop.playerSteamID) && drop.playerSteamID != job.NpcId) return false;
            var pc = ent as PlayerCorpse;
            if (pc != null && !(pc is NPCPlayerCorpse) && IsSteamId(pc.playerSteamID) && pc.playerSteamID != job.NpcId) return false;
            return Vector3.Distance(job.Position, ent.transform.position) <= 8f;
        }

        private bool EntityLootEmpty(BaseEntity entity)
        {
            if (entity == null) return true;
            var corpse = entity as LootableCorpse;
            if (corpse != null) return CorpseLootEmpty(corpse);
            var drop = entity as DroppedItemContainer;
            if (drop != null)
                return drop.inventory == null || drop.inventory.itemList == null || drop.inventory.itemList.Count == 0;
            var storage = entity as StorageContainer;
            if (storage != null && storage.inventory != null)
                return storage.inventory.itemList == null || storage.inventory.itemList.Count == 0;
            return true;
        }

        private void EraseRemains(BaseEntity entity)
        {
            if (!_config.RemoveCorpse || entity == null || entity.IsDestroyed) return;
            if (entity is BasePlayer || IsVacuumBox(entity) || IsRealPlayerCorpse(entity)) return;

            var playerCorpse = entity as PlayerCorpse;
            if (playerCorpse != null) playerCorpse.blockBagDrop = true;
            SpillLeftovers(entity);
            ZeroDispenser(entity);
            var dying = entity;
            dying.Kill();
            timer.Once(0.15f, () =>
            {
                if (dying != null && !dying.IsDestroyed) dying.Kill();
            });
        }

        private void SpillLeftovers(BaseEntity entity)
        {
            if (entity == null) return;
            Vector3 pos = entity.transform.position + Vector3.up * 0.25f;
            var corpse = entity as LootableCorpse;
            if (corpse?.containers != null)
            {
                for (int i = 0; i < corpse.containers.Length; i++)
                {
                    if (i >= 1)
                    {
                        DiscardContainer(corpse.containers[i], true);
                        continue;
                    }
                    SpillContainer(corpse.containers[i], pos);
                }
            }
            var drop = entity as DroppedItemContainer;
            if (drop != null) SpillContainer(drop.inventory, pos);
        }

        private static void SpillContainer(ItemContainer container, Vector3 pos)
        {
            if (container?.itemList == null || container.itemList.Count == 0) return;
            var copy = CopyItems(container);
            for (int i = 0; i < copy.Count; i++)
            {
                var item = copy[i];
                if (item == null || item.amount <= 0) continue;
                if (IsOutfit(item))
                {
                    item.Remove();
                    continue;
                }
                item.Drop(pos, Vector3.up);
            }
        }

        private void ZeroDispenser(BaseEntity entity)
        {
            var dispenser = FindDispenser(entity);
            if (dispenser?.containedItems == null) return;
            for (int i = 0; i < dispenser.containedItems.Count; i++)
            {
                var entry = dispenser.containedItems[i];
                if (entry != null) entry.amount = 0f;
            }
        }

        private static bool OwnsRemnant(BaseEntity ent, PendingKill job)
        {
            if (ent == null || job == null) return false;
            if (job.NetId.Value != 0 && ent.net != null && ent.net.ID == job.NetId) return true;
            var corpse = ent as PlayerCorpse;
            if (corpse != null && job.NpcId != 0 && corpse.playerSteamID == job.NpcId) return true;
            var drop = ent as DroppedItemContainer;
            return drop != null && job.NpcId != 0 && drop.playerSteamID == job.NpcId;
        }

        private bool IsRealPlayerCorpse(BaseEntity entity)
        {
            var corpse = entity as PlayerCorpse;
            if (corpse == null || corpse is NPCPlayerCorpse) return false;
            return IsSteamId(corpse.playerSteamID);
        }

        private bool ExecuteVacuum(LootableCorpse corpse, PendingKill job)
        {
            if (corpse == null || corpse.IsDestroyed || job == null) return false;
            if (!PrepareKiller(job.Killer, corpse.transform.position)) return false;

            var session = new VacuumSession();
            string source = string.IsNullOrEmpty(job.Source) ? "npc" : job.Source;
            if (ShouldHarvest(job, source))
                Harvest(corpse, job.Killer, source, session);

            if (ZombieLootOnly(job))
                TakeZombieLoot(corpse, job.Killer, source, session);
            else if (source == "npc" || source == "zed")
                TakeHumanoidLoot(corpse, job.Killer, source, session);
            else
                TakeContainers(corpse.containers, job.Killer, source, session);

            if (HoldForHarvest(corpse, job, source, session))
            {
                if (session.Moved > 0 || session.Dropped > 0)
                    FinishVacuum(job.Killer, source, session, CorpseLabel(corpse));
                return false;
            }

            EraseRemains(corpse);
            if (session.Moved <= 0 && session.Dropped <= 0) return _config.RemoveCorpse;

            FinishVacuum(job.Killer, source, session, CorpseLabel(corpse));
            return true;
        }

        private bool ExecuteBackpack(DroppedItemContainer backpack, PendingKill job)
        {
            if (backpack == null || backpack.IsDestroyed || job == null) return false;
            if (!PrepareKiller(job.Killer, backpack.transform.position)) return false;
            if (backpack.inventory == null) return false;
            var session = new VacuumSession();
            string source = string.IsNullOrEmpty(job.Source) ? "npc" : job.Source;
            var one = new ItemContainer[] { backpack.inventory };
            TakeContainers(one, job.Killer, source, session);
            if (session.Moved <= 0 && session.Dropped <= 0 && (backpack.inventory.itemList == null || backpack.inventory.itemList.Count == 0))
            {
                EraseRemains(backpack);
                return _config.RemoveCorpse;
            }
            EraseRemains(backpack);
            FinishVacuum(job.Killer, source, session, "backpack");
            return true;
        }

        private bool ExecuteLoose(BaseEntity entity, PendingKill job)
        {
            if (entity == null || entity.IsDestroyed || job == null) return false;
            if (!PrepareKiller(job.Killer, entity.transform.position)) return false;
            if (ZombieLootOnly(job)) return false;
            var session = new VacuumSession();
            string source = string.IsNullOrEmpty(job.Source) ? "npc" : job.Source;

            var storage = entity as StorageContainer;
            if (storage != null && storage.inventory != null && !IsVacuumBox(storage))
            {
                var one = new ItemContainer[] { storage.inventory };
                TakeContainers(one, job.Killer, source, session);
            }

            var npc = entity as BasePlayer;
            if (npc != null && npc.inventory != null)
            {
                var bags = new ItemContainer[2];
                bags[0] = npc.inventory.containerMain;
                bags[1] = npc.inventory.containerBelt;
                TakeHumanoidBags(bags, job.Killer, source, session);
                DiscardContainer(npc.inventory.containerWear, true);
            }

            if (ShouldHarvest(job, source))
                Harvest(entity, job.Killer, source, session);

            if (HoldForHarvest(entity, job, source, session))
                return false;

            EraseRemains(entity);
            if (session.Moved <= 0 && session.Dropped <= 0) return _config.RemoveCorpse;
            FinishVacuum(job.Killer, source, session, SafeName(entity));
            return true;
        }

        private bool PrepareKiller(ulong killer, Vector3 pos)
        {
            if (!IsEnabled(killer) || !AllowedId(killer)) return false;
            var player = FindAny(killer);
            if (player != null && player.IsConnected)
            {
                if (!InVacuumRange(killer, pos))
                    return false;
                _feet[killer] = player.transform.position;
                EnsureRuntime(player);
                PinBoxes(killer);
            }
            return true;
        }

        private bool InVacuumRange(ulong killer, Vector3 pos)
        {
            if (_config.MaxDistance <= 0f) return true;
            var player = FindAny(killer);
            if (player == null || !player.IsConnected) return false;
            return Vector3.Distance(player.transform.position, pos) <= _config.MaxDistance;
        }

        private void HoldForRange(PendingKill job, BaseEntity entity)
        {
            if (job == null || job.Used) return;
            if (!job.RangeNoted)
            {
                job.RangeNoted = true;
                Note(job.Killer, "Loot is waiting. Walk within " + RangeLabel() + " of " + (string.IsNullOrEmpty(job.Label) ? "the body" : job.Label) + ".");
            }
            if (job.ApproachWatch) return;
            job.ApproachWatch = true;
            timer.Once(1f, () => WatchApproach(job, entity));
        }

        private void WatchApproach(PendingKill job, BaseEntity body)
        {
            if (_unloading || job == null || job.Used) return;
            if (Time.realtimeSinceStartup - job.Time > 600f)
            {
                job.Used = true;
                job.ApproachWatch = false;
                Note(job.Killer, (string.IsNullOrEmpty(job.Label) ? "That kill" : job.Label) + " was left on the ground. You did not get within " + RangeLabel() + ".");
                return;
            }
            if (body == null || body.IsDestroyed)
            {
                body = FindOwnedBody(job);
                if (body == null)
                {
                    job.ApproachWatch = false;
                    Note(job.Killer, (string.IsNullOrEmpty(job.Label) ? "That body" : job.Label) + " is gone. Loot was not taken.");
                    return;
                }
            }
            if (!InVacuumRange(job.Killer, body.transform.position))
            {
                timer.Once(1f, () => WatchApproach(job, body));
                return;
            }
            job.ApproachWatch = false;
            TryVacuumEntity(body, job);
            if (!job.Used && !job.ApproachWatch)
                timer.Once(0.75f, () => WatchApproach(job, body));
        }

        private BaseEntity FindOwnedBody(PendingKill job)
        {
            if (job == null) return null;
            BaseEntity best = null;
            bool bestIsCorpse = false;
            _scan.Clear();
            Vis.Entities(job.Position, 8f, _scan, -1, QueryTriggerInteraction.Collide);
            for (int i = 0; i < _scan.Count; i++)
            {
                var ent = _scan[i];
                if (ent == null || ent.IsDestroyed || ent is BasePlayer || !IsLootCandidate(ent, job)) continue;
                if (!OwnsRemnant(ent, job) && Vector3.Distance(job.Position, ent.transform.position) > 2.5f) continue;
                bool corpse = ent is LootableCorpse || ent is BaseCorpse;
                if (best != null && bestIsCorpse && !corpse) continue;
                best = ent;
                bestIsCorpse = corpse;
            }
            return best;
        }

        private void FinishVacuum(ulong killer, string source, VacuumSession session, string label)
        {
            if (session.Moved <= 0 && session.Dropped <= 0) return;
            Note(killer, "Vacuumed " + label + ". " + session.Moved + " stacks filed.");
            var player = FindAny(killer);
            if (player != null && _config.Notify && session.Moved > 0)
                Say(player, "Vacuumed2", label, session.Moved.ToString());
            if (player != null && player.IsConnected)
                RefreshCount(player);
            CaptureRuntime(killer);
            SaveData();
            if (_config.Debug)
                Puts("Vacuum " + label + " for " + killer + " moved " + session.Moved + " dropped " + session.Dropped);
        }

        private void TakeContainers(ItemContainer[] containers, ulong killer, string source, VacuumSession session)
        {
            if (containers == null) return;
            for (int c = 0; c < containers.Length; c++)
            {
                var container = containers[c];
                if (container?.itemList == null) continue;
                var copy = CopyItems(container);
                for (int i = 0; i < copy.Count; i++)
                    PlaceItem(killer, copy[i], source, session, null);
            }
        }

        private void TakeZombieLoot(LootableCorpse corpse, ulong killer, string source, VacuumSession session)
        {
            if (corpse?.containers == null || corpse.containers.Length == 0) return;
            var main = corpse.containers[0];
            if (main?.itemList == null) return;
            var copy = CopyItems(main);
            for (int i = 0; i < copy.Count; i++)
            {
                var item = copy[i];
                if (item?.info == null) continue;
                if (item.info.category == ItemCategory.Attire) continue;
                PlaceItem(killer, item, source, session, null);
            }
            DiscardZombieOutfit(corpse);
        }

        private void TakeHumanoidLoot(LootableCorpse corpse, ulong killer, string source, VacuumSession session)
        {
            if (corpse?.containers == null) return;
            int limit = corpse.containers.Length > 2 ? 2 : corpse.containers.Length;
            var bags = new ItemContainer[limit];
            for (int i = 0; i < limit; i++) bags[i] = corpse.containers[i];
            TakeHumanoidBags(bags, killer, source, session);
            DiscardHumanoidOutfit(corpse);
        }

        private void TakeHumanoidBags(ItemContainer[] containers, ulong killer, string source, VacuumSession session)
        {
            if (containers == null) return;
            for (int c = 0; c < containers.Length; c++)
            {
                var container = containers[c];
                if (container?.itemList == null) continue;
                var copy = CopyItems(container);
                for (int i = 0; i < copy.Count; i++)
                {
                    var item = copy[i];
                    if (item?.info == null || IsOutfit(item)) continue;
                    PlaceItem(killer, item, source, session, null);
                }
            }
        }

        private static void DiscardHumanoidOutfit(LootableCorpse corpse)
        {
            if (corpse?.containers == null) return;
            for (int c = 0; c < corpse.containers.Length; c++)
                DiscardContainer(corpse.containers[c], c >= 2);
        }

        private static void DiscardContainer(ItemContainer container, bool everything)
        {
            if (container?.itemList == null) return;
            var copy = CopyItems(container);
            for (int i = 0; i < copy.Count; i++)
            {
                var item = copy[i];
                if (item == null) continue;
                if (!everything && !IsOutfit(item)) continue;
                item.Remove();
            }
        }

        private static bool IsOutfit(Item item)
        {
            return item?.info != null && item.info.category == ItemCategory.Attire;
        }

        private static void DiscardZombieOutfit(LootableCorpse corpse)
        {
            if (corpse?.containers == null) return;
            for (int c = 0; c < corpse.containers.Length; c++)
            {
                var container = corpse.containers[c];
                if (container?.itemList == null) continue;
                var copy = CopyItems(container);
                for (int i = 0; i < copy.Count; i++)
                {
                    var item = copy[i];
                    if (item == null) continue;
                    bool outfit = c > 0 || IsOutfit(item);
                    if (!outfit) continue;
                    item.Remove();
                }
            }
        }

        private bool ShouldHarvest(PendingKill job, string source)
        {
            if (source != "animal") return false;
            return _config.HarvestAnimals;
        }

        private bool HoldForHarvest(BaseEntity entity, PendingKill job, string source, VacuumSession session)
        {
            if (!ShouldHarvest(job, source) || job == null || job.Scans >= 3) return false;
            if (HasHarvestable(entity)) return true;
            if (FindDispenser(entity) != null) return false;
            return session.Moved <= 0 && session.Dropped <= 0 && !CorpseHasItems(entity as LootableCorpse) && job.Scans < 2;
        }

        private void Harvest(BaseEntity entity, ulong killer, string source, VacuumSession session)
        {
            var dispenser = FindDispenser(entity);
            if (dispenser == null || dispenser.containedItems == null)
            {
                HarvestCollectible(entity, killer, source, session);
                return;
            }
            var player = FindAny(killer);
            for (int i = 0; i < dispenser.containedItems.Count; i++)
            {
                var entry = dispenser.containedItems[i];
                if (entry == null || entry.amount <= 0f || entry.itemDef == null) continue;
                float scaled = entry.amount * _config.HarvestMultiplier;
                int amount = Mathf.FloorToInt(scaled);
                if (amount < 1) amount = 1;
                entry.amount = 0f;
                GiveFresh(killer, player, entry.itemDef, amount, dispenser, source, session);
            }
        }

        private void HarvestCollectible(BaseEntity entity, ulong killer, string source, VacuumSession session)
        {
            if (entity == null) return;
            var collect = entity.GetComponent<CollectibleEntity>();
            if (collect == null) collect = entity.GetComponentInChildren<CollectibleEntity>();
            if (collect == null || collect.itemList == null) return;
            var player = FindAny(killer);
            for (int i = 0; i < collect.itemList.Length; i++)
            {
                var entry = collect.itemList[i];
                if (entry == null || entry.amount <= 0f || entry.itemDef == null) continue;
                int amount = Mathf.FloorToInt(entry.amount * _config.HarvestMultiplier);
                if (amount < 1) amount = 1;
                entry.amount = 0f;
                GiveFresh(killer, player, entry.itemDef, amount, null, source, session);
            }
        }

        private void GiveFresh(ulong killer, BasePlayer player, ItemDefinition def, int amount, ResourceDispenser dispenser, string source, VacuumSession session)
        {
            if (def == null || amount <= 0) return;
            int guard = 0;
            while (amount > 0 && guard++ < 1000)
            {
                var item = ItemManager.Create(def, 1);
                if (item == null) return;
                int max = item.MaxStackable();
                if (max < 1) max = def.stackable > 0 ? def.stackable : 1;
                if (max < 1) max = 1;
                int chunk = amount > max ? max : amount;
                item.amount = chunk;
                amount -= chunk;
                if (player != null && dispenser != null)
                    Interface.CallHook("OnDispenserGather", dispenser, player, item);
                if (item == null || item.amount <= 0) continue;
                if (item.parent != null) continue;
                PlaceItem(killer, item, source, session, null);
            }
        }

        private void PlaceItem(ulong killer, Item item, string source, VacuumSession session, string forcePage)
        {
            if (item == null || item.amount <= 0) return;
            if (forcePage == null && _config.GutFish && item.info != null && IsWholeFish(item.info.shortname))
            {
                GutItem(killer, item, session);
                return;
            }
            string page = forcePage ?? ResolvePage(item, source);
            Deposit(killer, item, page, session);
        }

        private void GutItem(ulong killer, Item fish, VacuumSession session)
        {
            if (fish?.info == null) return;
            int yield = FishYieldOf(fish.info.shortname) * Mathf.Max(1, fish.amount);
            var rawDef = ItemManager.FindItemDefinition(_config.GuttedFishShortname);
            if (rawDef == null || yield <= 0)
            {
                Deposit(killer, fish, "food", session);
                return;
            }
            var created = new List<Item>();
            int left = yield;
            int guard = 0;
            bool failed = false;
            while (left > 0 && guard++ < 1000)
            {
                var raw = ItemManager.Create(rawDef, 1);
                if (raw == null)
                {
                    failed = true;
                    break;
                }
                int max = raw.MaxStackable();
                if (max < 1) max = 1;
                int chunk = left > max ? max : left;
                raw.amount = chunk;
                left -= chunk;
                created.Add(raw);
            }
            if (failed && created.Count == 0)
            {
                Deposit(killer, fish, ResolvePage(fish, "animal"), session);
                return;
            }
            fish.RemoveFromContainer();
            fish.Remove();
            for (int i = 0; i < created.Count; i++)
                Deposit(killer, created[i], "food", session);
        }

        private void GutCaughtFish(BasePlayer player, ulong killer, Item fish)
        {
            var parent = fish.parent;
            int slot = fish.position;
            var session = new VacuumSession();
            EnsureRuntime(player);
            PinBoxes(killer);
            int yield = FishYieldOf(fish.info.shortname) * Mathf.Max(1, fish.amount);
            var rawDef = ItemManager.FindItemDefinition(_config.GuttedFishShortname);
            if (rawDef == null || yield <= 0) return;
            var created = new List<Item>();
            int left = yield;
            int guard = 0;
            while (left > 0 && guard++ < 1000)
            {
                var raw = ItemManager.Create(rawDef, 1);
                if (raw == null) break;
                int max = raw.MaxStackable();
                if (max < 1) max = 1;
                int chunk = left > max ? max : left;
                raw.amount = chunk;
                left -= chunk;
                created.Add(raw);
            }
            if (created.Count == 0) return;
            fish.RemoveFromContainer();
            bool any = false;
            for (int i = 0; i < created.Count; i++)
            {
                int before = session.Moved;
                Deposit(killer, created[i], "food", session);
                if (session.Moved > before || session.Dropped > 0) any = true;
            }
            if (!any)
            {
                for (int i = 0; i < created.Count; i++)
                {
                    if (created[i] != null && created[i].parent == null)
                        created[i].Remove();
                }
                if (parent != null) fish.MoveToContainer(parent, slot);
                else player.GiveItem(fish);
                return;
            }
            fish.Remove();
            RefreshCount(player);
            SaveData();
        }

        private void Deposit(ulong killer, Item item, string page, VacuumSession session)
        {
            Deposit(killer, item, page, session, 0);
        }

        private void Deposit(ulong killer, Item item, string page, VacuumSession session, int depth)
        {
            if (item == null || item.amount <= 0) return;
            if (depth > 40)
            {
                Overflow(killer, FindAny(killer), item, page, session, null);
                return;
            }
            page = NormalizePage(page);
            int max = StackCap(item);
            if (item.amount > max)
            {
                var split = item.SplitItem(item.amount - max);
                if (split != null) Deposit(killer, split, page, session, depth + 1);
            }
            if (item.amount <= 0) return;

            var player = FindAny(killer);
            Dictionary<string, StorageContainer> pages;
            bool hasBoxes = _boxes.TryGetValue(killer, out pages) && pages != null && pages.Count > 0;
            if (hasBoxes)
            {
                StorageContainer box;
                if (TryGetBox(killer, page, out box) && box?.inventory != null)
                {
                    bool filed = MergeInto(box.inventory, item);
                    if (!filed && item != null && item.amount > 0)
                        filed = item.MoveToContainer(box.inventory, -1, true);
                    if (filed)
                    {
                        CollapseStacks(box.inventory);
                        session.Moved += 1;
                        return;
                    }
                }
                if (player != null && player.IsConnected)
                {
                    Overflow(killer, player, item, page, session, pages);
                    return;
                }
                Overflow(killer, null, item, page, session, pages);
                return;
            }

            if (player != null && player.IsConnected)
            {
                EnsureRuntime(player);
                Deposit(killer, item, page, session, depth + 1);
                return;
            }

            if (SerializeIntoData(killer, item, page))
            {
                item.RemoveFromContainer();
                item.Remove();
                session.Moved += 1;
                return;
            }
            session.Dropped += 1;
        }

        private static int StackCap(Item item)
        {
            int cap = item?.info != null && item.info.stackable > 0 ? item.info.stackable : 1;
            try
            {
                int hooked = item.MaxStackable();
                if (hooked > cap) cap = hooked;
            }
            catch
            {
                // MaxStackable can throw if a stack plugin is mid-reload. The definition cap still applies.
            }
            return cap < 1 ? 1 : cap;
        }

        private static bool HasContents(Item item)
        {
            return item?.contents?.itemList != null && item.contents.itemList.Count > 0;
        }

        private bool StackCompatible(Item dest, Item incoming)
        {
            if (dest == null || incoming == null || dest == incoming) return false;
            if (dest.info == null || incoming.info == null) return false;
            if (dest.info.itemid != incoming.info.itemid) return false;
            if (dest.skin != incoming.skin) return false;
            if (HasContents(dest) || HasContents(incoming)) return false;
            if (dest.blueprintTarget != incoming.blueprintTarget) return false;
            if ((dest.name ?? string.Empty) != (incoming.name ?? string.Empty)) return false;
            if (dest.maxCondition > 0f || incoming.maxCondition > 0f)
            {
                if (Mathf.Abs(dest.condition - incoming.condition) > 0.5f) return false;
                if (Mathf.Abs(dest.maxCondition - incoming.maxCondition) > 0.5f) return false;
            }
            var left = dest.instanceData;
            var right = incoming.instanceData;
            if (left != null || right != null)
            {
                if (left == null || right == null) return false;
                if (left.dataInt != right.dataInt) return false;
                if (Mathf.Abs(left.dataFloat - right.dataFloat) > 1f) return false;
            }
            object hook = Interface.CallHook("CanStackItem", dest, incoming);
            if (hook is bool && !(bool)hook) return false;
            return true;
        }

        // MoveToContainer(-1) opens a new slot when CanStack sees a "full" vanilla pile.
        // Fill a matching stack up to the Stack Size Controller cap first.
        private bool MergeInto(ItemContainer container, Item incoming)
        {
            if (container?.itemList == null || incoming == null || incoming.amount <= 0) return false;
            if (HasContents(incoming) || StackCap(incoming) <= 1) return false;
            int count = container.itemList.Count;
            for (int i = 0; i < count && incoming.amount > 0; i++)
            {
                if (i >= container.itemList.Count) break;
                var dest = container.itemList[i];
                if (!StackCompatible(dest, incoming)) continue;
                int room = StackCap(dest) - dest.amount;
                if (room <= 0) continue;
                int move = incoming.amount > room ? room : incoming.amount;
                dest.amount += move;
                incoming.amount -= move;
                dest.MarkDirty();
            }
            if (incoming.amount > 0) return false;
            incoming.RemoveFromContainer();
            incoming.Remove();
            return true;
        }

        private void CollapseStacks(ItemContainer container)
        {
            if (container?.itemList == null || container.itemList.Count < 2) return;
            for (int i = container.itemList.Count - 1; i >= 1; i--)
            {
                if (i >= container.itemList.Count) continue;
                var incoming = container.itemList[i];
                if (incoming == null || incoming.amount <= 0 || HasContents(incoming)) continue;
                int earlier = i;
                for (int d = 0; d < earlier && incoming.amount > 0; d++)
                {
                    if (d >= container.itemList.Count) break;
                    var dest = container.itemList[d];
                    if (!StackCompatible(dest, incoming)) continue;
                    int room = StackCap(dest) - dest.amount;
                    if (room <= 0) continue;
                    int move = incoming.amount > room ? room : incoming.amount;
                    dest.amount += move;
                    incoming.amount -= move;
                    dest.MarkDirty();
                }
                if (incoming.amount > 0)
                {
                    incoming.MarkDirty();
                    continue;
                }
                incoming.RemoveFromContainer();
                incoming.Remove();
            }
        }

        private static Vector3 boxPosition(Dictionary<string, StorageContainer> pages)
        {
            foreach (var pair in pages)
            {
                if (pair.Value != null && !pair.Value.IsDestroyed)
                    return pair.Value.transform.position;
            }
            return new Vector3(0f, 50f, 0f);
        }

        private void Overflow(ulong killer, BasePlayer player, Item item, string page, VacuumSession session, Dictionary<string, StorageContainer> pages)
        {
            if (item == null) return;
            if (IsOutfit(item))
            {
                item.RemoveFromContainer();
                item.Remove();
                return;
            }
            WarnFull(player, page, session);
            if (player != null && player.IsConnected)
                item.Drop(player.GetDropPosition(), player.GetDropVelocity());
            else
            {
                Vector3 dropAt;
                if (!_feet.TryGetValue(killer, out dropAt))
                    dropAt = pages != null ? boxPosition(pages) : new Vector3(0f, 50f, 0f);
                item.Drop(dropAt + Vector3.up, Vector3.up);
            }
            if (session != null) session.Dropped += 1;
        }

        private void WarnFull(BasePlayer player, string page, VacuumSession session)
        {
            if (player == null || !_config.WarnWhenFull || session == null) return;
            if (session.FullPages == null) session.FullPages = new HashSet<string>();
            if (!session.FullPages.Add(page)) return;
            Say(player, "PageFullGround", PageTitle(page));
            ShowFullWarning(player, session);
        }

        private void ShowFullWarning(BasePlayer player, VacuumSession session)
        {
            if (player == null || !player.IsConnected || session?.FullPages == null || session.FullPages.Count == 0) return;
            var names = new List<string>();
            foreach (var full in session.FullPages)
                names.Add(PageTitle(full).ToUpperInvariant());
            names.Sort(StringComparer.Ordinal);
            string text = string.Join(" + ", names.ToArray()) + (names.Count == 1 ? " PAGE FULL" : " PAGES FULL");
            CuiHelper.DestroyUi(player, UiFull);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.72 0.08 0.07 0.96" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = "-280 168", OffsetMax = "280 200" }
            }, "Overlay", UiFull);
            ui.Add(new CuiElement
            {
                Parent = UiFull,
                Components =
                {
                    new CuiTextComponent { Text = text, FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1", Font = "RobotoCondensed-Bold.ttf" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
                }
            });
            CuiHelper.AddUi(player, ui);
            ulong id = Id(player);
            int stamp;
            _fullWarn.TryGetValue(id, out stamp);
            stamp++;
            _fullWarn[id] = stamp;
            int shown = stamp;
            timer.Once(6f, () =>
            {
                int now;
                if (!_fullWarn.TryGetValue(id, out now) || now != shown) return;
                var still = FindAny(id);
                if (still != null && still.IsConnected) CuiHelper.DestroyUi(still, UiFull);
            });
        }

        private string ResolvePage(Item item, string source)
        {
            if (item?.info == null) return "misc";
            string sn = item.info.shortname ?? string.Empty;
            if (Listed(_config.KeepOnSource, sn)) return "misc";

            string cat = null;
            if (Listed(_config.FoodItems, sn) || LooksLikeFood(sn))
                cat = "food";
            else if (Listed(_config.MedItems, sn))
                cat = "meds";
            else if (Listed(_config.AmmoItems, sn))
                cat = "ammo";
            else if (Listed(_config.WeaponItems, sn))
                cat = "weapons";
            else
                cat = PageForCategory(item.info.category);

            if (string.IsNullOrEmpty(cat) || !CategoryEnabled(cat)) return "misc";
            return cat;
        }

        private static string PageForCategory(ItemCategory category)
        {
            switch (category)
            {
                case ItemCategory.Weapon: return "weapons";
                case ItemCategory.Construction: return "construction";
                case ItemCategory.Items: return "items";
                case ItemCategory.Resources: return "resources";
                case ItemCategory.Attire: return "attire";
                case ItemCategory.Tool: return "tools";
                case ItemCategory.Medical: return "meds";
                case ItemCategory.Food: return "food";
                case ItemCategory.Ammunition: return "ammo";
                case ItemCategory.Traps: return "traps";
                case ItemCategory.Misc: return "misc";
                case ItemCategory.Component: return "components";
                case ItemCategory.Electrical: return "electrical";
                case ItemCategory.Fun: return "fun";
                default: return null;
            }
        }

        private bool LooksLikeFood(string shortname)
        {
            string n = shortname.ToLower();
            if (n.IndexOf("meat", StringComparison.Ordinal) >= 0) return true;
            if (n.StartsWith("fish.")) return true;
            return false;
        }

        private bool CategoryEnabled(string page)
        {
            if (page == "misc") return true;
            if (_config.EnabledCategories == null) return true;
            bool enabled;
            if (_config.EnabledCategories.TryGetValue(page, out enabled)) return enabled;
            return true;
        }

        private bool IsWholeFish(string shortname)
        {
            if (string.IsNullOrEmpty(shortname)) return false;
            string n = shortname.ToLower();
            if (!n.StartsWith("fish.")) return false;
            if (n.IndexOf("raw", StringComparison.Ordinal) >= 0) return false;
            if (n.IndexOf("cooked", StringComparison.Ordinal) >= 0) return false;
            if (n.IndexOf("spoiled", StringComparison.Ordinal) >= 0) return false;
            if (Listed(_config.FishIgnore, shortname)) return false;
            return true;
        }

        private int FishYieldOf(string shortname)
        {
            if (_config.FishYield != null)
            {
                foreach (var pair in _config.FishYield)
                {
                    if (string.Equals(pair.Key, shortname, StringComparison.OrdinalIgnoreCase))
                        return Mathf.Max(1, pair.Value);
                }
            }
            return Mathf.Max(1, _config.DefaultFishYield);
        }

        #endregion

        #region Classify

        private ulong ResolveKillerId(BaseCombatEntity victim, HitInfo info)
        {
            var player = PlayerFromHit(info);
            if (IsRealPlayer(player)) return Id(player);
            var last = victim != null ? victim.lastAttacker as BasePlayer : null;
            if (IsRealPlayer(last)) return Id(last);
            var lastEnt = victim != null ? victim.lastAttacker as BaseEntity : null;
            if (lastEnt != null && IsSteamId(lastEnt.OwnerID))
            {
                var owner = FindAny(lastEnt.OwnerID);
                if (IsRealPlayer(owner)) return Id(owner);
            }
            if (!_config.CreditOwnedTraps || info == null || info.Initiator == null) return 0;
            var turret = info.Initiator as AutoTurret;
            if (turret == null) turret = info.Initiator.GetComponent<AutoTurret>();
            if (turret != null && IsSteamId(turret.OwnerID)) return turret.OwnerID;
            var trap = info.Initiator as GunTrap;
            if (trap == null) trap = info.Initiator.GetComponent<GunTrap>();
            if (trap != null && IsSteamId(trap.OwnerID)) return trap.OwnerID;
            return 0;
        }

        private BasePlayer PlayerFromHit(HitInfo info)
        {
            if (info == null) return null;
            if (IsRealPlayer(info.InitiatorPlayer)) return info.InitiatorPlayer;
            var direct = info.Initiator as BasePlayer;
            if (IsRealPlayer(direct)) return direct;
            var weapon = info.Weapon;
            if (weapon != null)
            {
                var owner = weapon.GetOwnerPlayer();
                if (IsRealPlayer(owner)) return owner;
            }
            var ent = info.Initiator;
            for (int i = 0; i < 5 && ent != null; i++)
            {
                var asPlayer = ent as BasePlayer;
                if (IsRealPlayer(asPlayer)) return asPlayer;
                if (IsSteamId(ent.OwnerID))
                {
                    var owned = FindAny(ent.OwnerID);
                    if (IsRealPlayer(owned)) return owned;
                }
                ent = ent.GetParentEntity();
            }
            return null;
        }

        private PendingKill MatchBackpack(DroppedItemContainer backpack)
        {
            if (backpack == null) return null;
            float now = Time.realtimeSinceStartup;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var job = _pending[i];
                if (job.Used || Time.realtimeSinceStartup - job.Time > 12f) continue;
                if (job.NpcId == 0 || backpack.playerSteamID != job.NpcId) continue;
                if (now - job.Time > 15f) continue;
                if (Vector3.Distance(job.Position, backpack.transform.position) > 8f) continue;
                return job;
            }
            return null;
        }

        private PendingKill MatchSpawn(BaseEntity entity)
        {
            PrunePending();
            PendingKill best = null;
            float bestDist = CorpseMatchRadius;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var job = _pending[i];
                if (Time.realtimeSinceStartup - job.Time > 12f) continue;
                if (!IsLootCandidate(entity, job)) continue;
                var pc = entity as PlayerCorpse;
                if (pc != null && job.NpcId != 0 && pc.playerSteamID == job.NpcId)
                    return job;
                var drop = entity as DroppedItemContainer;
                if (drop != null && job.NpcId != 0 && drop.playerSteamID == job.NpcId)
                    return job;
                float dist = Vector3.Distance(job.Position, entity.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = job;
                }
            }
            return best;
        }

        private bool CouldBeLoot(BaseEntity entity)
        {
            if (entity == null || entity is BasePlayer || IsVacuumBox(entity)) return false;
            if (entity is LootableCorpse || entity is DroppedItemContainer || entity is BaseCorpse) return true;
            string blob = Blob(entity);
            return blob.Contains("corpse") || blob.Contains("murderer") || blob.Contains("scarecrow");
        }

        private bool IsLootCandidate(BaseEntity ent, PendingKill job)
        {
            if (ent == null || ent.IsDestroyed || job == null || IsVacuumBox(ent)) return false;
            if (ent is BasePlayer)
            {
                if (ZombieLootOnly(job)) return false;
                var npc = (BasePlayer)ent;
                if (!npc.IsDead()) return false;
                return job.NpcId != 0 && Id(npc) == job.NpcId;
            }

            var pc = ent as PlayerCorpse;
            if (pc != null)
            {
                if (job.Source == "animal") return false;
                if (job.NpcId != 0 && pc.playerSteamID == job.NpcId) return true;
                bool npcCorpse = pc is NPCPlayerCorpse;
                bool namedZed = IsZombieBlob(pc.ShortPrefabName, pc.PrefabName, pc.playerName);
                if (!npcCorpse && !namedZed) return false;
                if (IsSteamId(pc.playerSteamID) && pc.playerSteamID != job.NpcId && !namedZed) return false;
                return job.Source == "zed" || job.Source == "npc";
            }

            var drop = ent as DroppedItemContainer;
            if (drop != null)
            {
                if (ZombieLootOnly(job)) return false;
                if (job.Source == "animal") return false;
                if (IsSteamId(drop.playerSteamID) && drop.playerSteamID != job.NpcId) return false;
                if (job.NpcId != 0 && drop.playerSteamID == job.NpcId) return true;
                return job.Source == "zed" || job.Source == "npc";
            }

            if (ent is LootableCorpse || ent is BaseCorpse)
            {
                if (IsRealPlayerCorpse(ent)) return false;
                if (job.Source == "animal" || job.Source == "zed" || job.Source == "npc") return true;
            }

            var storage = ent as StorageContainer;
            if (storage != null)
            {
                if (job.Source == "animal") return false;
                return IsZombieCorpseName(ent);
            }

            if (job.Source == "animal")
            {
                var combat = ent as BaseCombatEntity;
                if (combat != null && !combat.IsDead()) return false;
                var dispenser = FindDispenser(ent);
                if (dispenser != null && dispenser.gatherType == ResourceDispenser.GatherType.Flesh) return true;
                return Listed(_config.AnimalKeywords, Blob(ent));
            }

            return false;
        }

        private bool IsZombieCorpseName(BaseEntity ent)
        {
            return IsZombieBlob(ent.ShortPrefabName, ent.PrefabName, ent.ShortPrefabName);
        }

        private void PrunePending()
        {
            float now = Time.realtimeSinceStartup;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Used) 
                {
                    _pending.RemoveAt(i);
                    continue;
                }
                float age = now - _pending[i].Time;
                if (age > 600f || (age > 20f && !_pending[i].ApproachWatch))
                    _pending.RemoveAt(i);
            }
        }

        private string ClassifySource(BaseCombatEntity entity)
        {
            if (entity == null) return null;
            var npc = entity as BasePlayer;
            if (npc != null)
            {
                if (!LooksLikeNpc(npc)) return null;
                if (IsZombieActor(npc)) return "zed";
                return "npc";
            }
            if (IsSteamId(entity.OwnerID)) return null;
            if (IsAnimalEntity(entity)) return "animal";
            if (IsZombieBlob(entity.ShortPrefabName, entity.PrefabName, entity.GetType().Name)) return "zed";
            return null;
        }

        private bool LooksLikeNpc(BasePlayer npc)
        {
            if (npc == null) return false;
            if (npc.IsNpc) return true;
            if (npc.GetType() == typeof(BasePlayer)) return false;
            string type = npc.GetType().Name ?? string.Empty;
            if (type.IndexOf("NPC", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (type.IndexOf("Scientist", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (type.IndexOf("Scarecrow", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (type.IndexOf("Murderer", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (type.IndexOf("Zombie", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (IsZombieBlob(npc.ShortPrefabName, npc.PrefabName, string.Empty)) return true;
            return HasZombieBehaviour(npc);
        }

        private bool IsZombieActor(BaseEntity entity)
        {
            if (entity == null) return false;
            var npc = entity as BasePlayer;
            string display = npc != null ? npc.displayName : string.Empty;
            if (IsZombieBlob(entity.ShortPrefabName, entity.PrefabName, display)) return true;
            if (IsZombieBlob(entity.GetType().Name, string.Empty, string.Empty)) return true;
            return HasZombieBehaviour(entity);
        }

        private bool HasZombieBehaviour(BaseEntity entity)
        {
            if (entity == null) return false;
            var behaviours = entity.GetComponents<MonoBehaviour>();
            if (behaviours == null) return false;
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null) continue;
                string name = behaviours[i].GetType().Name ?? string.Empty;
                if (name.IndexOf("Zombie", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (name.IndexOf("Horde", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private bool IsAnimalEntity(BaseCombatEntity entity)
        {
            if (entity == null || entity is BasePlayer || entity is BaseCorpse) return false;
            string type = entity.GetType().Name ?? string.Empty;
            if (type.IndexOf("Animal", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string blob = Blob(entity) + " " + type;
            if (Listed(_config.AnimalKeywords, blob) || LooksLikeAnimal(blob)) return true;
            var dispenser = FindDispenser(entity);
            return dispenser != null && dispenser.gatherType == ResourceDispenser.GatherType.Flesh;
        }

        private static bool LooksLikeAnimal(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return false;
            return blob.IndexOf("tiger", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("panther", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("crocodile", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("croc", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("snake", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("bear", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("boar", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("wolf", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("stag", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("deer", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("chicken", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("horse", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("shark", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ResourceDispenser FindDispenser(BaseEntity entity)
        {
            if (entity == null) return null;
            var dispenser = entity.GetComponent<ResourceDispenser>();
            if (dispenser != null) return dispenser;
            dispenser = entity.GetComponentInChildren<ResourceDispenser>();
            if (dispenser != null) return dispenser;
            var parent = entity.GetParentEntity();
            if (parent == null) return null;
            dispenser = parent.GetComponent<ResourceDispenser>();
            if (dispenser != null) return dispenser;
            return parent.GetComponentInChildren<ResourceDispenser>();
        }

        private bool LootReady(BaseEntity entity)
        {
            if (entity == null) return false;
            var corpse = entity as LootableCorpse;
            if (corpse != null && corpse.containers == null) return false;
            var drop = entity as DroppedItemContainer;
            if (drop != null && drop.inventory == null) return false;
            return true;
        }

        private bool HasHarvestable(BaseEntity entity)
        {
            var dispenser = FindDispenser(entity);
            if (dispenser?.containedItems == null) return false;
            for (int i = 0; i < dispenser.containedItems.Count; i++)
            {
                var entry = dispenser.containedItems[i];
                if (entry != null && entry.amount > 0.01f) return true;
            }
            return false;
        }

        private static bool CorpseHasItems(LootableCorpse corpse)
        {
            if (corpse?.containers == null) return false;
            for (int i = 0; i < corpse.containers.Length; i++)
            {
                var container = corpse.containers[i];
                if (container?.itemList != null && container.itemList.Count > 0) return true;
            }
            return false;
        }

        private bool IsZombieBlob(string a, string b, string c)
        {
            return Listed(_config.ZombieKeywords, (a ?? "") + " " + (b ?? "") + " " + (c ?? ""));
        }

        private static string Blob(BaseEntity entity)
        {
            if (entity == null) return string.Empty;
            return ((entity.ShortPrefabName ?? string.Empty) + " " + (entity.PrefabName ?? string.Empty)).ToLower();
        }

        private static string SafeName(BaseEntity entity)
        {
            if (entity == null) return "entity";
            var npc = entity as BasePlayer;
            if (npc != null && !string.IsNullOrEmpty(npc.displayName)) return npc.displayName;
            if (!string.IsNullOrEmpty(entity.ShortPrefabName)) return entity.ShortPrefabName;
            return entity.GetType().Name;
        }

        private static string CorpseLabel(LootableCorpse corpse)
        {
            var playerCorpse = corpse as PlayerCorpse;
            if (playerCorpse != null && !string.IsNullOrEmpty(playerCorpse.playerName))
                return playerCorpse.playerName;
            if (!string.IsNullOrEmpty(corpse.ShortPrefabName))
                return corpse.ShortPrefabName.Replace(".corpse", "").Replace("_", " ");
            return "corpse";
        }

        private static bool CorpseLootEmpty(LootableCorpse corpse)
        {
            if (corpse.containers == null) return true;
            for (int i = 0; i < corpse.containers.Length; i++)
            {
                var container = corpse.containers[i];
                if (container?.itemList != null && container.itemList.Count > 0) return false;
            }
            return true;
        }

        #endregion

        #region Boxes

        private void HandleConnect(BasePlayer player)
        {
            if (player == null || player.IsNpc || !player.IsConnected) return;
            if (!IsSteamId(Id(player))) return;
            EnsureRuntime(player);
            DrawButton(player);
        }

        private void EnsureRuntime(BasePlayer player)
        {
            if (player == null) return;
            ulong id = Id(player);
            Dictionary<string, StorageContainer> pages;
            if (!_boxes.TryGetValue(id, out pages) || pages == null)
            {
                pages = new Dictionary<string, StorageContainer>();
                _boxes[id] = pages;
            }
            var store = GetStore(id);
            for (int i = 0; i < PageOrder.Length; i++)
            {
                string page = PageOrder[i];
                StorageContainer existing;
                if (pages.TryGetValue(page, out existing) && existing != null && !existing.IsDestroyed)
                    continue;
                var box = CreateBox(id, page, HiddenSpot(id));
                if (box == null) continue;
                pages[page] = box;
                List<StoredItem> saved;
                if (store.Pages.TryGetValue(page, out saved))
                    RestorePage(player, box, saved);
            }
            PinBoxes(id);
            ChillBoxes(id);
        }

        private StorageContainer CreateBox(ulong owner, string page, Vector3 pos)
        {
            var entity = GameManager.server.CreateEntity(BoxPrefab, pos);
            var box = entity as StorageContainer;
            if (box == null)
            {
                if (entity != null) entity.Kill();
                return null;
            }
            box.skinID = MarkerSkin;
            box.OwnerID = owner;
            box.limitNetworking = true;
            box.syncPosition = false;
            box.EnableSaving(false);
            box.Spawn();
            if (box.inventory != null)
            {
                int slots = Mathf.Clamp(_config.SlotsPerPage, 6, 48);
                box.inventory.capacity = slots;
                box.inventory.allowedContents = ItemContainer.ContentsType.Generic;
                box.inventory.canAcceptItem = null;
                box.inventory.maxStackSize = 0;
                Chill(box.inventory);
            }
            var colliders = box.GetComponentsInChildren<Collider>();
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].enabled = false;
            var decay = box as DecayEntity;
            if (decay != null) decay.CancelInvoke("DecayTick");
            if (box.net != null) _boxOwner[box.net.ID.Value] = owner;
            box.name = "killvacuum:" + owner + ":" + page;
            return box;
        }

        private void RestorePage(BasePlayer player, StorageContainer box, List<StoredItem> items)
        {
            if (box?.inventory == null || items == null) return;
            var orphans = new List<Item>();
            for (int i = 0; i < items.Count; i++)
            {
                var item = DeserializeItem(items[i], orphans);
                if (item == null) continue;
                if (MergeInto(box.inventory, item)) continue;
                if (item.amount <= 0) continue;
                if (!item.MoveToContainer(box.inventory, -1, true))
                    orphans.Add(item);
            }
            CollapseStacks(box.inventory);
            for (int i = 0; i < orphans.Count; i++)
            {
                var extra = orphans[i];
                if (extra == null || extra.amount <= 0) continue;
                if (extra.parent != null) continue;
                if (player != null)
                    extra.Drop(player.GetDropPosition(), player.GetDropVelocity());
                else
                    extra.Drop(box.transform.position + Vector3.up, Vector3.up);
            }
        }

        private void CaptureRuntime(ulong playerId)
        {
            Dictionary<string, StorageContainer> pages;
            if (!_boxes.TryGetValue(playerId, out pages) || pages == null) return;
            var store = GetStore(playerId);
            for (int i = 0; i < PageOrder.Length; i++)
            {
                string page = PageOrder[i];
                StorageContainer box;
                if (!pages.TryGetValue(page, out box) || box?.inventory == null)
                    continue;
                var list = new List<StoredItem>();
                var items = box.inventory.itemList;
                if (items != null)
                {
                    for (int n = 0; n < items.Count; n++)
                    {
                        var stored = SerializeItem(items[n], 0);
                        if (stored != null) list.Add(stored);
                    }
                }
                store.Pages[page] = list;
            }
        }

        private bool SerializeIntoData(ulong playerId, Item item, string page)
        {
            var stored = SerializeItem(item, 0);
            if (stored == null) return false;
            var store = GetStore(playerId);
            if (!store.Pages.ContainsKey(page) || store.Pages[page] == null)
                store.Pages[page] = new List<StoredItem>();
            store.Pages[page].Add(stored);
            return true;
        }

        private StoredItem SerializeItem(Item item, int depth)
        {
            if (item?.info == null || depth > 4) return null;
            var data = new StoredItem
            {
                ItemId = item.info.itemid,
                Amount = item.amount,
                Skin = item.skin,
                Condition = item.condition,
                MaxCondition = item.maxCondition,
                Position = item.position,
                Flags = (int)item.flags,
                Name = item.name,
                Text = item.text,
                BlueprintTarget = item.blueprintTarget,
                Ammo = -1
            };
            if (item.instanceData != null)
            {
                data.DataInt = item.instanceData.dataInt;
                data.DataFloat = item.instanceData.dataFloat;
                data.HasInstance = true;
            }
            var projectile = item.GetHeldEntity() as BaseProjectile;
            if (projectile != null && projectile.primaryMagazine != null)
            {
                data.Ammo = projectile.primaryMagazine.contents;
                if (projectile.primaryMagazine.ammoType != null)
                    data.AmmoType = projectile.primaryMagazine.ammoType.shortname;
            }
            if (item.contents?.itemList != null && item.contents.itemList.Count > 0)
            {
                data.Contents = new List<StoredItem>();
                for (int i = 0; i < item.contents.itemList.Count; i++)
                {
                    var child = SerializeItem(item.contents.itemList[i], depth + 1);
                    if (child != null) data.Contents.Add(child);
                }
            }
            return data;
        }

        private Item DeserializeItem(StoredItem data, List<Item> orphans)
        {
            if (data == null || data.Amount <= 0) return null;
            var item = ItemManager.CreateByItemID(data.ItemId, data.Amount, data.Skin);
            if (item == null) return null;
            if (data.MaxCondition > 0f) item.maxCondition = data.MaxCondition;
            item.condition = data.MaxCondition > 0f ? Mathf.Clamp(data.Condition, 0f, item.maxCondition) : data.Condition;
            item.flags = (Item.Flag)data.Flags;
            if (!string.IsNullOrEmpty(data.Name)) item.name = data.Name;
            if (!string.IsNullOrEmpty(data.Text)) item.text = data.Text;
            if (data.BlueprintTarget != 0) item.blueprintTarget = data.BlueprintTarget;
            if (data.HasInstance || data.DataInt != 0)
            {
                if (item.instanceData == null) item.instanceData = new ProtoBuf.Item.InstanceData();
                if (data.DataInt != 0) item.instanceData.dataInt = data.DataInt;
                if (data.HasInstance) item.instanceData.dataFloat = data.DataFloat;
                item.instanceData.ShouldPool = false;
            }
            if (data.Contents != null && data.Contents.Count > 0)
            {
                if (item.contents == null)
                {
                    item.contents = new ItemContainer();
                    item.contents.ServerInitialize(item, Math.Max(4, data.Contents.Count));
                    item.contents.GiveUID();
                }
                for (int i = 0; i < data.Contents.Count; i++)
                {
                    var child = DeserializeItem(data.Contents[i], orphans);
                    if (child == null) continue;
                    if (!child.MoveToContainer(item.contents) && orphans != null)
                        orphans.Add(child);
                }
            }
            var projectile = item.GetHeldEntity() as BaseProjectile;
            if (projectile != null && projectile.primaryMagazine != null && data.Ammo >= 0)
            {
                if (!string.IsNullOrEmpty(data.AmmoType))
                {
                    var ammo = ItemManager.FindItemDefinition(data.AmmoType);
                    if (ammo != null) projectile.primaryMagazine.ammoType = ammo;
                }
                projectile.primaryMagazine.contents = data.Ammo;
            }
            if (data.Position >= 0) item.position = data.Position;
            return item;
        }

        private bool TryGetBox(ulong playerId, string page, out StorageContainer box)
        {
            box = null;
            Dictionary<string, StorageContainer> pages;
            if (!_boxes.TryGetValue(playerId, out pages) || pages == null) return false;
            return pages.TryGetValue(page, out box) && box != null && !box.IsDestroyed;
        }

        private static Vector3 HiddenSpot(ulong owner)
        {
            float x = (owner % 2500UL) * 0.2f;
            float z = ((owner / 2500UL) % 2500UL) * 0.2f;
            return new Vector3(x, -500f, z);
        }

        private void ChillBoxes(ulong playerId)
        {
            if (!_config.Refrigerate) return;
            Dictionary<string, StorageContainer> pages;
            if (!_boxes.TryGetValue(playerId, out pages) || pages == null) return;
            foreach (var pair in pages)
            {
                if (pair.Value?.inventory != null)
                    Chill(pair.Value.inventory);
            }
        }

        private void Chill(ItemContainer container)
        {
            if (container == null || !_config.Refrigerate) return;
            var entity = container.entityOwner;
            if (entity == null || entity.IsDestroyed) return;
            if (entity.GetComponent<VacuumFridge>() != null) return;
            entity.gameObject.AddComponent<VacuumFridge>();
        }

        // Same hook the powered fridge and Backpacks use. Returning 0 pauses the spoil timer.
        private class VacuumFridge : EntityComponent<StorageContainer>, IFoodSpoilModifier
        {
            public float GetSpoilMultiplier(Item item) => 0f;
        }

        private void PinBoxes(ulong playerId)
        {
            Dictionary<string, StorageContainer> pages;
            if (!_boxes.TryGetValue(playerId, out pages) || pages == null) return;
            Vector3 hidden = HiddenSpot(playerId);
            foreach (var pair in pages)
            {
                if (pair.Value != null && !pair.Value.IsDestroyed)
                    pair.Value.transform.position = hidden;
            }
        }

        private void DestroyBoxes(ulong playerId)
        {
            Dictionary<string, StorageContainer> pages;
            if (!_boxes.TryGetValue(playerId, out pages)) return;
            _boxes.Remove(playerId);
            if (pages == null) return;
            foreach (var pair in pages)
            {
                var box = pair.Value;
                if (box == null || box.IsDestroyed) continue;
                if (box.net != null) _boxOwner.Remove(box.net.ID.Value);
                box.Kill();
            }
        }

        private void DestroyAllBoxes()
        {
            var ids = new List<ulong>(_boxes.Keys);
            for (int i = 0; i < ids.Count; i++)
                DestroyBoxes(ids[i]);
        }

        private void SweepOrphans()
        {
            var kill = new List<BaseEntity>();
            foreach (var entity in BaseNetworkable.serverEntities)
            {
                var box = entity as StorageContainer;
                if (box != null && box.skinID == MarkerSkin)
                    kill.Add(box);
            }
            for (int i = 0; i < kill.Count; i++)
            {
                if (kill[i] != null && !kill[i].IsDestroyed)
                    kill[i].Kill();
            }
        }

        private void NoteActivity(ItemContainer container)
        {
            var entity = container != null ? container.entityOwner as StorageContainer : null;
            if (entity == null || entity.net == null) return;
            ulong owner;
            if (!_boxOwner.TryGetValue(entity.net.ID.Value, out owner)) return;
            MarkDirty(owner);
        }

        private bool IsVacuumBox(BaseEntity entity)
        {
            var box = entity as StorageContainer;
            if (box == null || box.skinID != MarkerSkin) return false;
            if (box.net == null) return true;
            return _boxOwner.ContainsKey(box.net.ID.Value) || box.skinID == MarkerSkin;
        }

        private bool IsVacuumContainer(ItemContainer container)
        {
            var entity = container != null ? container.entityOwner : null;
            return entity != null && IsVacuumBox(entity);
        }

        private bool IsLootingVacuum(BasePlayer player)
        {
            var loot = player?.inventory?.loot;
            if (loot == null || !loot.IsLooting()) return false;
            return IsVacuumBox(loot.entitySource);
        }

        #endregion

        #region UI

        private void OpenVacuum(BasePlayer player, string page)
        {
            if (player == null || !player.IsConnected) return;
            if (!Allowed(player))
            {
                Say(player, "NoPerm");
                return;
            }
            page = NormalizePage(page);
            ulong id = Id(player);
            EnsureRuntime(player);
            StorageContainer box;
            if (!TryGetBox(id, page, out box))
            {
                Say(player, "NoBox");
                return;
            }
            var store = GetStore(id);
            store.LastPage = page;
            _openPage[id] = page;
            _feet[id] = player.transform.position;
            PinBoxes(id);
            if (box.inventory != null) CollapseStacks(box.inventory);
            if (player.inventory.loot.IsLooting())
                player.EndLooting();
            timer.Once(0.05f, () =>
            {
                if (player == null || !player.IsConnected || box == null || box.IsDestroyed) return;
                var loot = player.inventory.loot;
                loot.Clear();
                loot.PositionChecks = false;
                loot.entitySource = box;
                loot.itemSource = null;
                loot.AddContainer(box.inventory);
                loot.SendImmediate();
                box.limitNetworking = false;
                box.SetFlag(BaseEntity.Flags.Open, true);
                player.ClientRPC(RpcTarget.Player("RPC_OpenLootPanel", player), box.panelName);
                if (IsMoving(player)) DrawGrid(player);
                RefreshCount(player);
                DrawRail(player, page);
                if (IsMoving(player)) DrawPad(player);
            });
        }

        private void DrawButton(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            CuiHelper.DestroyUi(player, UiButton);
            CuiHelper.DestroyUi(player, UiCount);
            _buttonDrawn.Remove(Id(player));
            if (!Allowed(player)) return;
            ulong id = Id(player);
            bool on = IsEnabled(id);
            string aMin;
            string aMax;
            string oMin;
            string oMax;
            PieceRect(player, true, out aMin, out aMax, out oMin, out oMax);
            bool movingButton = MoveTarget(player) == "button";
            string image = ButtonImageUrl();
            var ui = new CuiElementContainer();
            if (image == null)
            {
                ui.Add(new CuiButton
                {
                    Button = { Color = on ? _config.ColorOn : _config.ColorOff, Command = movingButton ? "killvacuum.noop" : "killvacuum.open" },
                    RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax },
                    Text = { Text = string.Empty }
                }, "Overlay", UiButton);
                ui.Add(new CuiElement
                {
                    Parent = UiButton,
                    Components =
                    {
                        new CuiTextComponent { Text = movingButton ? "MOVE" : "VAC", FontSize = 18, Align = TextAnchor.MiddleCenter, Color = "0.95 0.90 0.82 1", Font = "RobotoCondensed-Bold.ttf" },
                        new CuiRectTransformComponent { AnchorMin = "0 0.28", AnchorMax = "1 0.92" }
                    }
                });
            }
            else
            {
                ui.Add(new CuiElement
                {
                    Parent = "Overlay",
                    Name = UiButton,
                    Components =
                    {
                        new CuiRawImageComponent { Url = image, Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
                    }
                });
                ui.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = movingButton ? "killvacuum.noop" : "killvacuum.open" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    Text = { Text = string.Empty }
                }, UiButton);
            }
            AddCount(ui, player, image != null);
            CuiHelper.AddUi(player, ui);
            _buttonDrawn.Add(id);
        }

        private void AddCount(CuiElementContainer ui, BasePlayer player, bool image)
        {
            bool on = IsEnabled(Id(player));
            bool movingButton = MoveTarget(player) == "button";
            string status = movingButton ? (image ? "MOVE" : "TAP") : (on ? "ON" : "OFF") + "  " + CountStacks(Id(player));
            string color = movingButton ? "0.95 0.90 0.82 0.9" : (on ? "0.45 0.95 0.28 1" : "0.95 0.22 0.18 1");
            ui.Add(new CuiElement
            {
                Name = UiCount,
                Parent = UiButton,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = status,
                        FontSize = image ? 12 : 10,
                        Align = TextAnchor.MiddleCenter,
                        Color = color,
                        Font = "RobotoCondensed-Bold.ttf"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = image ? "-0.4 -0.36" : "0 0.02",
                        AnchorMax = image ? "1.4 -0.02" : "1 0.32"
                    }
                }
            });
        }

        private void RefreshCount(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (!_buttonDrawn.Contains(Id(player)))
            {
                DrawButton(player);
                return;
            }
            CuiHelper.DestroyUi(player, UiCount);
            var ui = new CuiElementContainer();
            AddCount(ui, player, ButtonImageUrl() != null);
            CuiHelper.AddUi(player, ui);
        }

        private string ButtonImageUrl()
        {
            var url = _config == null ? null : _config.ButtonImageUrl;
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
            return url;
        }

        private void DrawRail(BasePlayer player, string active)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiRail);
            ulong id = Id(player);
            bool on = IsEnabled(id);
            string aMin;
            string aMax;
            string oMin;
            string oMax;
            PieceRect(player, false, out aMin, out aMax, out oMin, out oMax);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.09 0.07 0.94" },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = oMin, OffsetMax = oMax }
            }, "Overlay", UiRail);
            ui.Add(new CuiElement
            {
                Parent = UiRail,
                Components =
                {
                    new CuiTextComponent { Text = "VACUUM", FontSize = 16, Align = TextAnchor.MiddleLeft, Color = "0.88 0.48 0.18 1", Font = "RobotoCondensed-Bold.ttf" },
                    new CuiRectTransformComponent { AnchorMin = "0.04 0.915", AnchorMax = "0.62 0.985" }
                }
            });
            ui.Add(new CuiButton
            {
                Button = { Color = IsMoving(player) ? "0.55 0.28 0.10 0.95" : "0.16 0.18 0.13 0.95", Command = "killvacuum.movecycle" },
                RectTransform = { AnchorMin = "0.64 0.918", AnchorMax = "0.96 0.982" },
                Text = { Text = IsMoving(player) ? "MOVING" : "MOVE", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.95 0.90 0.82 1", Font = "RobotoCondensed-Bold.ttf" }
            }, UiRail);
            ui.Add(new CuiButton
            {
                Button = { Color = on ? "0.55 0.28 0.10 0.95" : "0.20 0.16 0.12 0.95", Command = "killvacuum.toggle" },
                RectTransform = { AnchorMin = "0.04 0.835", AnchorMax = "0.96 0.905" },
                Text = { Text = on ? "VACUUM ON" : "VACUUM OFF", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "0.95 0.90 0.82 1", Font = "RobotoCondensed-Bold.ttf" }
            }, UiRail);

            int rows = (CategoryPages.Length + 1) / 2;
            float gap = 0.008f;
            float top = 0.815f;
            float bottom = 0.02f;
            float row = (top - bottom - gap * (rows - 1)) / rows;
            for (int i = 0; i < CategoryPages.Length; i++)
            {
                int column = i % 2;
                int rowIndex = i / 2;
                float yMax = top - rowIndex * (row + gap);
                float yMin = yMax - row;
                string xMin = column == 0 ? "0.04" : "0.51";
                string xMax = column == 0 ? "0.49" : "0.96";
                AddPageButton(ui, id, CategoryPages[i], active, xMin, xMax, yMin, yMax, 12);
            }
            CuiHelper.AddUi(player, ui);
        }

        private void AddPageButton(CuiElementContainer ui, ulong id, string page, string active, string xMin, string xMax, float yMin, float yMax, int font)
        {
            bool selected = page == active;
            int count = CountPage(id, page);
            ui.Add(new CuiButton
            {
                Button = { Color = selected ? "0.55 0.28 0.10 0.95" : "0.16 0.18 0.13 0.95", Command = "killvacuum.page " + page },
                RectTransform =
                {
                    AnchorMin = xMin + " " + yMin.ToString("0.000", CultureInfo.InvariantCulture),
                    AnchorMax = xMax + " " + yMax.ToString("0.000", CultureInfo.InvariantCulture)
                },
                Text = { Text = PageTitle(page) + "  " + count, FontSize = font, Align = TextAnchor.MiddleCenter, Color = "0.95 0.90 0.82 1", Font = "RobotoCondensed-Bold.ttf" }
            }, UiRail);
        }

        private void DrawPad(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiPad);
            if (player == null || !IsMoving(player)) return;
            string target = MoveTarget(player);
            bool button = target == "button";
            string aMin;
            string aMax;
            string oMin;
            string oMax;
            PieceRect(player, button, out aMin, out aMax, out oMin, out oMax);
            float x1;
            float y1;
            float x2;
            float y2;
            ParsePair(oMin, out x1, out y1);
            ParsePair(oMax, out x2, out y2);
            var store = GetStore(Id(player));
            bool free = button ? store.ButtonFree : store.RailFree;
            float ax = button ? store.ButtonAX : store.RailAX;
            bool padOnLeft = free && ax > 0.62f;
            float padW = 104f;
            float padH = 250f;
            float midY = (y1 + y2) * 0.5f;
            string padMin = padOnLeft ? Pair(x1 - 8f - padW, midY - padH * 0.5f) : Pair(x2 + 8f, midY - padH * 0.5f);
            string padMax = padOnLeft ? Pair(x1 - 8f, midY + padH * 0.5f) : Pair(x2 + 8f + padW, midY + padH * 0.5f);
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.09 0.07 0.96" },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = padMin, OffsetMax = padMax },
                CursorEnabled = true
            }, "Overlay", UiPad);
            ui.Add(new CuiElement
            {
                Parent = UiPad,
                Components =
                {
                    new CuiTextComponent { Text = button ? "BUTTON" : "PANEL", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.88 0.48 0.18 1", Font = "RobotoCondensed-Bold.ttf" },
                    new CuiRectTransformComponent { AnchorMin = "0.06 0.90", AnchorMax = "0.94 0.99" }
                }
            });
            AddPadButton(ui, "UP", "killvacuum.nudge up", "0.18 0.78", "0.82 0.88");
            AddPadButton(ui, "LT", "killvacuum.nudge left", "0.06 0.66", "0.48 0.76");
            AddPadButton(ui, "RT", "killvacuum.nudge right", "0.52 0.66", "0.94 0.76");
            AddPadButton(ui, "DN", "killvacuum.nudge down", "0.18 0.54", "0.82 0.64");
            AddPadButton(ui, "LEFT", "killvacuum.snap left", "0.06 0.42", "0.48 0.52");
            AddPadButton(ui, "RIGHT", "killvacuum.snap right", "0.52 0.42", "0.94 0.52");
            AddPadButton(ui, button ? "PANEL" : "BUTTON", "killvacuum.moveswap", "0.06 0.30", "0.94 0.40");
            AddPadButton(ui, "RESET", "killvacuum.uireset", "0.06 0.18", "0.48 0.28");
            AddPadButton(ui, "DONE", "killvacuum.movedone", "0.52 0.18", "0.94 0.28");
            CuiHelper.AddUi(player, ui);
        }

        private void DrawGrid(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiGrid);
            if (player == null || !IsMoving(player)) return;
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.15" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", UiGrid);
            for (int r = 0; r < GridRows; r++)
            {
                for (int c = 0; c < GridCols; c++)
                {
                    float xMin = c / (float)GridCols;
                    float xMax = (c + 1f) / GridCols;
                    float yMin = r / (float)GridRows;
                    float yMax = (r + 1f) / GridRows;
                    ui.Add(new CuiButton
                    {
                        Button = { Color = "0.95 0.90 0.82 0.04", Command = "killvacuum.place " + c + " " + r },
                        RectTransform =
                        {
                            AnchorMin = xMin.ToString("0.000", CultureInfo.InvariantCulture) + " " + yMin.ToString("0.000", CultureInfo.InvariantCulture),
                            AnchorMax = xMax.ToString("0.000", CultureInfo.InvariantCulture) + " " + yMax.ToString("0.000", CultureInfo.InvariantCulture)
                        },
                        Text = { Text = string.Empty, FontSize = 1, Align = TextAnchor.MiddleCenter }
                    }, UiGrid);
                }
            }
            CuiHelper.AddUi(player, ui);
        }

        private void AddPadButton(CuiElementContainer ui, string label, string command, string min, string max)
        {
            ui.Add(new CuiButton
            {
                Button = { Color = "0.22 0.18 0.12 0.96", Command = command },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = label, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "0.95 0.90 0.82 1", Font = "RobotoCondensed-Bold.ttf" }
            }, UiPad);
        }

        private void DestroyUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiButton);
            CuiHelper.DestroyUi(player, UiCount);
            CuiHelper.DestroyUi(player, UiRail);
            CuiHelper.DestroyUi(player, UiPad);
            CuiHelper.DestroyUi(player, UiGrid);
            CuiHelper.DestroyUi(player, UiFull);
            _buttonDrawn.Remove(Id(player));
        }

        private int CountStacks(ulong playerId)
        {
            int total = 0;
            for (int i = 0; i < PageOrder.Length; i++)
                total += CountPage(playerId, PageOrder[i]);
            return total;
        }

        private int CountPage(ulong playerId, string page)
        {
            StorageContainer box;
            if (TryGetBox(playerId, page, out box) && box.inventory != null)
                return LiveCount(box.inventory);
            var store = GetStore(playerId);
            List<StoredItem> list;
            if (store.Pages.TryGetValue(page, out list) && list != null) return list.Count;
            return 0;
        }

        private static int LiveCount(ItemContainer container)
        {
            if (container?.itemList == null) return 0;
            int total = 0;
            for (int i = 0; i < container.itemList.Count; i++)
            {
                var item = container.itemList[i];
                if (item != null && item.amount > 0) total++;
            }
            return total;
        }

        private static void DropEmpty(ItemContainer container)
        {
            if (container?.itemList == null) return;
            for (int i = container.itemList.Count - 1; i >= 0; i--)
            {
                var item = container.itemList[i];
                if (item == null || item.amount > 0) continue;
                item.RemoveFromContainer();
                item.Remove();
            }
        }

        private void QueueCount(ItemContainer container)
        {
            var entity = container != null ? container.entityOwner : null;
            if (entity?.net == null) return;
            ulong owner;
            if (!_boxOwner.TryGetValue(entity.net.ID.Value, out owner) || owner == 0) return;
            if (!_uiQueued.Add(owner)) return;
            timer.Once(0.15f, () =>
            {
                _uiQueued.Remove(owner);
                var player = FindAny(owner);
                if (player == null || !player.IsConnected) return;
                RefreshCount(player);
            });
        }

        private void PieceRect(BasePlayer player, bool button, out string anchorMin, out string anchorMax, out string offMin, out string offMax)
        {
            var store = GetStore(Id(player));
            bool free = button ? store.ButtonFree : store.RailFree;
            float w = button ? ButtonW : RailW;
            float h = button ? ButtonH : RailH;
            if (!free)
            {
                anchorMin = button ? "0.5 0" : "0 0.5";
                anchorMax = anchorMin;
                if (button)
                {
                    offMin = Shift(_config.ButtonMin, store.ButtonX, store.ButtonY);
                    offMax = Shift(_config.ButtonMax, store.ButtonX, store.ButtonY);
                }
                else
                {
                    offMin = Shift(_config.RailMin, store.RailX, store.RailY);
                    offMax = Shift(_config.RailMax, store.RailX, store.RailY);
                }
                return;
            }
            float ax = Mathf.Clamp(button ? store.ButtonAX : store.RailAX, 0f, 1f);
            float ay = Mathf.Clamp(button ? store.ButtonAY : store.RailAY, 0f, 1f);
            float nx = button ? store.ButtonX : store.RailX;
            float ny = button ? store.ButtonY : store.RailY;
            anchorMin = ax.ToString("0.000", CultureInfo.InvariantCulture) + " " + ay.ToString("0.000", CultureInfo.InvariantCulture);
            anchorMax = anchorMin;
            offMin = Pair(-w * 0.5f + nx, -h * 0.5f + ny);
            offMax = Pair(w * 0.5f + nx, h * 0.5f + ny);
        }

        private void ButtonRect(BasePlayer player, out string min, out string max)
        {
            var store = GetStore(Id(player));
            min = Shift(_config.ButtonMin, store.ButtonX, store.ButtonY);
            max = Shift(_config.ButtonMax, store.ButtonX, store.ButtonY);
        }

        private void RailRect(BasePlayer player, out string min, out string max)
        {
            var store = GetStore(Id(player));
            min = Shift(_config.RailMin, store.RailX, store.RailY);
            max = Shift(_config.RailMax, store.RailX, store.RailY);
        }

        private static string Shift(string baseline, float dx, float dy)
        {
            float x;
            float y;
            ParsePair(baseline, out x, out y);
            return Pair(x + dx, y + dy);
        }

        private static void ParsePair(string raw, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            if (string.IsNullOrEmpty(raw)) return;
            var parts = raw.Split(' ');
            if (parts.Length < 2) return;
            float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        private static string Pair(float x, float y)
        {
            return x.ToString("0.#", CultureInfo.InvariantCulture) + " " + y.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private bool IsMoving(BasePlayer player)
        {
            if (player == null) return false;
            string target;
            return _moveTarget.TryGetValue(Id(player), out target) && !string.IsNullOrEmpty(target);
        }

        private string MoveTarget(BasePlayer player)
        {
            string target;
            if (player != null && _moveTarget.TryGetValue(Id(player), out target)) return target;
            return null;
        }

        private void SetMoveTarget(BasePlayer player, string target)
        {
            if (player == null) return;
            ulong id = Id(player);
            if (string.IsNullOrEmpty(target))
            {
                _moveTarget.Remove(id);
                CuiHelper.DestroyUi(player, UiPad);
                CuiHelper.DestroyUi(player, UiGrid);
                DrawButton(player);
                if (IsLootingVacuum(player))
                {
                    string page;
                    if (!_openPage.TryGetValue(id, out page)) page = GetStore(id).LastPage;
                    DrawRail(player, page);
                }
                else CuiHelper.DestroyUi(player, UiRail);
                Say(player, "MoveDone");
                return;
            }
            _moveTarget[id] = target;
            RefreshMoveUi(player);
            Say(player, "Moving", target == "button" ? "VAC button" : "panel");
        }

        private void Nudge(BasePlayer player, string target, float dx, float dy, bool throttle)
        {
            if (player == null || string.IsNullOrEmpty(target)) return;
            if (Mathf.Abs(dx) < 0.01f && Mathf.Abs(dy) < 0.01f) return;
            ulong id = Id(player);
            var store = GetStore(id);
            if (target == "button")
            {
                store.ButtonX += dx;
                store.ButtonY += dy;
            }
            else
            {
                store.RailX += dx;
                store.RailY += dy;
            }
            MarkDirty(id);
            if (throttle)
            {
                float now = Time.realtimeSinceStartup;
                float next;
                if (_nextUiMove.TryGetValue(id, out next) && now < next)
                {
                    if (_uiTrailing.Add(id))
                    {
                        timer.Once(0.06f, () =>
                        {
                            _uiTrailing.Remove(id);
                            if (player != null && player.IsConnected && IsMoving(player))
                                RefreshMoveUi(player);
                        });
                    }
                    return;
                }
                _nextUiMove[id] = now + 0.05f;
            }
            RefreshMoveUi(player);
        }

        private void RefreshMoveUi(BasePlayer player)
        {
            if (player == null) return;
            ulong id = Id(player);
            if (IsMoving(player)) DrawGrid(player);
            else CuiHelper.DestroyUi(player, UiGrid);
            DrawButton(player);
            if (IsLootingVacuum(player) || IsMoving(player))
            {
                string page;
                if (!_openPage.TryGetValue(id, out page)) page = GetStore(id).LastPage;
                DrawRail(player, page);
            }
            if (IsMoving(player)) DrawPad(player);
        }

        private void ApplyFree(BasePlayer player, string target, float ax, float ay, float nx, float ny)
        {
            if (player == null || string.IsNullOrEmpty(target)) return;
            var store = GetStore(Id(player));
            if (target == "button")
            {
                store.ButtonFree = true;
                store.ButtonAX = ax;
                store.ButtonAY = ay;
                store.ButtonX = nx;
                store.ButtonY = ny;
            }
            else
            {
                store.RailFree = true;
                store.RailAX = ax;
                store.RailAY = ay;
                store.RailX = nx;
                store.RailY = ny;
            }
            MarkDirty(Id(player));
            RefreshMoveUi(player);
        }

        private void PlaceAtCell(BasePlayer player, int col, int row)
        {
            string target = MoveTarget(player);
            if (string.IsNullOrEmpty(target)) target = "rail";
            col = Mathf.Clamp(col, 0, GridCols - 1);
            row = Mathf.Clamp(row, 0, GridRows - 1);
            float ax = (col + 0.5f) / GridCols;
            float ay = (row + 0.5f) / GridRows;
            float inset = target == "button" ? 0.05f : 0.08f;
            ax = Mathf.Clamp(ax, inset, 1f - inset);
            ay = Mathf.Clamp(ay, inset, 1f - inset);
            ApplyFree(player, target, ax, ay, 0f, 0f);
        }

        private void SnapSide(BasePlayer player, string side)
        {
            string target = MoveTarget(player);
            if (string.IsNullOrEmpty(target)) target = "rail";
            bool button = target == "button";
            bool right = side == "right";
            float w = button ? ButtonW : RailW;
            float ax = right ? 1f : 0f;
            float ay = button ? 0.16f : 0.5f;
            float nx = right ? -(12f + w * 0.5f) : (12f + w * 0.5f);
            ApplyFree(player, target, ax, ay, nx, 0f);
            Say(player, "Snapped", button ? "VAC button" : "panel", right ? "right" : "left");
        }

        #endregion

        #region Commands

        [ChatCommand("vacuum")]
        private void CmdVacuum(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            if (!Allowed(player))
            {
                Say(player, "NoPerm");
                return;
            }
            if (args == null || args.Length == 0)
            {
                Toggle(player);
                return;
            }
            string head = args[0].ToLower();
            if (head == "on" || head == "off")
            {
                SetEnabled(player, head == "on");
                return;
            }
            if (head == "open")
            {
                string page = args.Length > 1 ? args[1] : GetStore(Id(player)).LastPage;
                OpenVacuum(player, page);
                return;
            }
            if (head == "move" || head == "ui")
            {
                string mode = args.Length > 1 ? args[1].ToLower() : string.Empty;
                if (mode == "done" || mode == "stop" || mode == "off")
                {
                    SetMoveTarget(player, null);
                    return;
                }
                if (mode == "reset")
                {
                    ResetUi(player);
                    return;
                }
                if (mode == "left" || mode == "right")
                {
                    if (!IsMoving(player)) SetMoveTarget(player, MoveTarget(player) ?? "rail");
                    SnapSide(player, mode);
                    return;
                }
                if (mode == "button" || mode == "vac")
                {
                    SetMoveTarget(player, "button");
                    return;
                }
                if (mode == "rail" || mode == "panel")
                {
                    SetMoveTarget(player, "rail");
                    return;
                }
                string current = MoveTarget(player);
                if (string.IsNullOrEmpty(current)) SetMoveTarget(player, "rail");
                else if (current == "rail") SetMoveTarget(player, "button");
                else SetMoveTarget(player, null);
                return;
            }
            if (head == "clear" || head == "empty")
            {
                if (args.Length < 3 || !string.Equals(args[args.Length - 1], "confirm", StringComparison.OrdinalIgnoreCase))
                {
                    Say(player, "ClearConfirm");
                    return;
                }
                ClearPages(player, args[1]);
                return;
            }
            if (head == "last")
            {
                string note;
                if (!_lastNote.TryGetValue(Id(player), out note) || string.IsNullOrEmpty(note))
                    note = "No kill tracked yet.";
                player.ChatMessage("<color=#e07a2f>Vacuum</color>  " + note);
                return;
            }
            if (head == "range" || head == "distance")
            {
                Say(player, "Range", RangeLabel());
                return;
            }
            if (head == "status")
            {
                ulong id = Id(player);
                Say(player, "Status", IsEnabled(id) ? "ON" : "OFF", CountStacks(id).ToString(), RangeLabel());
                return;
            }
            Say(player, "Help");
        }

        [ConsoleCommand("killvacuum.open")]
        private void CmdOpen(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (IsLootingVacuum(player))
            {
                player.EndLooting();
                return;
            }
            string page = GetStore(Id(player)).LastPage;
            OpenVacuum(player, page);
        }

        [ConsoleCommand("killvacuum.page")]
        private void CmdPage(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            OpenVacuum(player, arg.GetString(0, "misc"));
        }

        [ConsoleCommand("killvacuum.toggle")]
        private void CmdToggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!Allowed(player))
            {
                Say(player, "NoPerm");
                return;
            }
            Toggle(player);
            if (IsLootingVacuum(player) || IsMoving(player))
            {
                string page;
                if (!_openPage.TryGetValue(Id(player), out page)) page = GetStore(Id(player)).LastPage;
                DrawRail(player, page);
            }
        }

        [ConsoleCommand("killvacuum.movecycle")]
        private void CmdMoveCycle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !Allowed(player)) return;
            string current = MoveTarget(player);
            if (string.IsNullOrEmpty(current)) SetMoveTarget(player, "rail");
            else if (current == "rail") SetMoveTarget(player, "button");
            else SetMoveTarget(player, null);
        }

        [ConsoleCommand("killvacuum.place")]
        private void CmdPlace(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !Allowed(player)) return;
            if (!IsMoving(player)) SetMoveTarget(player, "rail");
            PlaceAtCell(player, arg.GetInt(0, 0), arg.GetInt(1, 0));
        }

        [ConsoleCommand("killvacuum.snap")]
        private void CmdSnap(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !Allowed(player)) return;
            if (!IsMoving(player)) SetMoveTarget(player, "rail");
            string side = (arg.GetString(0, "left") ?? "left").ToLower();
            SnapSide(player, side == "right" ? "right" : "left");
        }

        [ConsoleCommand("killvacuum.moveswap")]
        private void CmdMoveSwap(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !Allowed(player)) return;
            SetMoveTarget(player, MoveTarget(player) == "button" ? "rail" : "button");
        }

        [ConsoleCommand("killvacuum.movedone")]
        private void CmdMoveDone(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            SetMoveTarget(player, null);
        }

        [ConsoleCommand("killvacuum.uireset")]
        private void CmdUiReset(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !Allowed(player)) return;
            ResetUi(player);
        }

        [ConsoleCommand("killvacuum.nudge")]
        private void CmdNudge(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !Allowed(player)) return;
            string target = MoveTarget(player);
            if (string.IsNullOrEmpty(target)) target = "rail";
            string dir = (arg.GetString(0, string.Empty) ?? string.Empty).ToLower();
            float step = _config.UiMoveStep;
            float dx = 0f;
            float dy = 0f;
            if (dir == "left" || dir == "lt") dx = -step;
            else if (dir == "right" || dir == "rt") dx = step;
            else if (dir == "up") dy = step;
            else if (dir == "down" || dir == "dn") dy = -step;
            else
            {
                float.TryParse(arg.GetString(0, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out dx);
                float.TryParse(arg.GetString(1, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out dy);
            }
            Nudge(player, target, dx, dy, false);
        }

        [ConsoleCommand("killvacuum.noop")]
        private void CmdNoop(ConsoleSystem.Arg arg)
        {
        }

        [ConsoleCommand("killvacuum.wipe")]
        private void CmdWipe(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !HasAdmin(arg.Player()))
            {
                arg.ReplyWith("killvacuum.admin required");
                return;
            }
            if (!arg.HasArgs(1))
            {
                arg.ReplyWith("killvacuum.wipe <steamid|all>");
                return;
            }
            if (string.Equals(arg.GetString(0), "all", StringComparison.OrdinalIgnoreCase))
            {
                DestroyAllBoxes();
                _store = new Dictionary<ulong, PlayerStore>();
                SaveData();
                foreach (var online in BasePlayer.activePlayerList)
                    HandleConnect(online);
                arg.ReplyWith("wiped every Kill Vacuum buffer");
                return;
            }
            ulong id;
            if (!ulong.TryParse(arg.GetString(0), out id))
            {
                arg.ReplyWith("bad steamid");
                return;
            }
            var player = FindAny(id);
            if (player != null) ClearPages(player, "all");
            else
            {
                var store = GetStore(id);
                for (int i = 0; i < PageOrder.Length; i++)
                    store.Pages[PageOrder[i]] = new List<StoredItem>();
                SaveData();
            }
            arg.ReplyWith("wiped " + id);
        }

        private void Toggle(BasePlayer player) => SetEnabled(player, !IsEnabled(Id(player)));

        private void SetEnabled(BasePlayer player, bool enabled)
        {
            var store = GetStore(Id(player));
            store.Enabled = enabled;
            MarkDirty(Id(player));
            DrawButton(player);
            Say(player, "Toggled", enabled ? "ON" : "OFF");
        }

        private void ResetUi(BasePlayer player)
        {
            var store = GetStore(Id(player));
            store.RailX = 0f;
            store.RailY = 0f;
            store.ButtonX = 0f;
            store.ButtonY = 0f;
            store.RailFree = false;
            store.ButtonFree = false;
            store.RailAX = 0f;
            store.RailAY = 0.5f;
            store.ButtonAX = 0.5f;
            store.ButtonAY = 0f;
            MarkDirty(Id(player));
            DrawButton(player);
            if (IsLootingVacuum(player) || IsMoving(player))
            {
                string page;
                if (!_openPage.TryGetValue(Id(player), out page)) page = store.LastPage;
                DrawRail(player, page);
            }
            if (IsMoving(player)) DrawPad(player);
            Say(player, "MoveReset");
        }

        private void ClearPages(BasePlayer player, string page)
        {
            ulong id = Id(player);
            EnsureRuntime(player);
            bool all = string.Equals(page, "all", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i < PageOrder.Length; i++)
            {
                if (!all && !string.Equals(PageOrder[i], NormalizePage(page), StringComparison.OrdinalIgnoreCase))
                    continue;
                StorageContainer box;
                if (!TryGetBox(id, PageOrder[i], out box) || box.inventory == null) continue;
                var copy = CopyItems(box.inventory);
                for (int n = 0; n < copy.Count; n++)
                {
                    if (copy[n] != null) copy[n].Remove();
                }
            }
            CaptureRuntime(id);
            SaveData();
            RefreshCount(player);
            Say(player, "Cleared", all ? "every page" : PageTitle(NormalizePage(page)));
        }

        #endregion

        #region Helpers

        private class VacuumSession
        {
            public int Moved;
            public int Dropped;
            public HashSet<string> FullPages;
        }

        private bool Allowed(BasePlayer player)
        {
            if (player == null) return false;
            if (_config.AdminsBypass && player.IsAdmin) return true;
            return permission.UserHasPermission(player.UserIDString, PermUse);
        }

        private bool AllowedId(ulong playerId)
        {
            var player = FindAny(playerId);
            if (player != null) return Allowed(player);
            return permission.UserHasPermission(playerId.ToString(), PermUse);
        }

        private bool HasAdmin(BasePlayer player)
        {
            if (player == null) return false;
            if (player.IsAdmin) return true;
            return permission.UserHasPermission(player.UserIDString, PermAdmin);
        }

        private bool IsEnabled(ulong playerId) => GetStore(playerId).Enabled;

        private static bool IsRealPlayer(BasePlayer player)
        {
            if (player == null || player.IsNpc) return false;
            return IsSteamId(Id(player));
        }

        private static bool IsSteamId(ulong id) => id > 76561197960265728UL;

        private static ulong Id(BasePlayer player) => (ulong)player.userID;

        private static BasePlayer FindAny(ulong id)
        {
            var player = BasePlayer.FindByID(id);
            if (player != null) return player;
            return BasePlayer.FindSleeping(id);
        }

        private static List<Item> CopyItems(ItemContainer container)
        {
            var copy = new List<Item>();
            if (container?.itemList == null) return copy;
            for (int i = 0; i < container.itemList.Count; i++)
                copy.Add(container.itemList[i]);
            return copy;
        }

        private static bool Listed(List<string> list, string blob)
        {
            if (list == null || string.IsNullOrEmpty(blob)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (string.IsNullOrEmpty(entry)) continue;
                if (blob.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static bool IsPage(string page)
        {
            if (string.IsNullOrEmpty(page)) return false;
            for (int i = 0; i < PageOrder.Length; i++)
                if (PageOrder[i] == page) return true;
            return false;
        }

        private static string NormalizePage(string page)
        {
            if (string.IsNullOrEmpty(page)) return "misc";
            string n = page.ToLower();
            if (n == "npc" || n == "zed" || n == "zombie" || n == "zombies" || n == "zeds") return "misc";
            if (n == "animal" || n == "animals" || n == "meat") return "misc";
            if (n == "med" || n == "medical") return "meds";
            if (n == "weapon" || n == "guns") return "weapons";
            if (n == "fish") return "food";
            if (n == "build" || n == "building" || n == "construction") return "construction";
            if (n == "resource" || n == "resources" || n == "mats") return "resources";
            if (n == "component" || n == "components" || n == "comps" || n == "parts") return "components";
            if (n == "clothes" || n == "clothing" || n == "attire") return "attire";
            if (n == "tool" || n == "tools") return "tools";
            if (n == "item" || n == "items") return "items";
            if (n == "trap" || n == "traps") return "traps";
            if (n == "electric" || n == "electrical" || n == "power") return "electrical";
            if (n == "misc" || n == "other" || n == "mics") return "misc";
            if (n == "ammo" || n == "ammunition") return "ammo";
            if (n == "fun") return "fun";
            for (int i = 0; i < PageOrder.Length; i++)
                if (PageOrder[i] == n) return n;
            return "misc";
        }

        private static string PageTitle(string page)
        {
            switch (page)
            {
                case "npc": return "NPC";
                case "zed": return "Zed";
                case "animal": return "Animal";
                case "weapons": return "Weapons";
                case "construction": return "Construct";
                case "items": return "Items";
                case "resources": return "Resources";
                case "attire": return "Attire";
                case "tools": return "Tools";
                case "meds": return "Meds";
                case "food": return "Food";
                case "ammo": return "Ammo";
                case "traps": return "Traps";
                case "misc": return "Misc";
                case "components": return "Components";
                case "electrical": return "Electric";
                case "fun": return "Fun";
                default: return page;
            }
        }

        private string RangeLabel()
        {
            if (_config.MaxDistance <= 0f) return "unlimited";
            return _config.MaxDistance.ToString("0.#", CultureInfo.InvariantCulture) + "m";
        }

        private void Note(ulong playerId, string text)
        {
            if (playerId == 0 || string.IsNullOrEmpty(text)) return;
            _lastNote[playerId] = text;
        }

        private void Say(BasePlayer player, string key, params object[] args)
        {
            if (player == null) return;
            string msg = lang.GetMessage(key, this, player.UserIDString);
            if (args != null && args.Length > 0)
            {
                try { msg = string.Format(msg, args); }
                catch { }
            }
            player.ChatMessage("<color=#e07a2f>Vacuum</color>  " + msg);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPerm"] = "You do not have permission to use the kill vacuum.",
                ["Toggled"] = "Vacuum is {0}. The buffer still opens either way.",
                ["Vacuumed"] = "Took {0}. {1} stacks filed. Main page {2}.",
                ["Vacuumed2"] = "Took {0}. {1} stacks filed. Corpse and body bag removed.",
                ["PageFull"] = "{0} page is full - LOOT ON GROUND!",
                ["PageFullGround"] = "{0} page is full - LOOT ON GROUND!",
                ["ClearConfirm"] = "Nothing was deleted. Use /vacuum clear <page|all> confirm",
                ["Cleared"] = "Cleared {0}.",
                ["Status"] = "Vacuum {0}. {1} stacks in the buffer. Range {2}.",
                ["Range"] = "Kills count from any distance. Loot is pulled only when you are within {0} of the body. 0 is unlimited.",
                ["NoBox"] = "That page is not available yet.",
                ["Moving"] = "Tap the screen to drop the {0}. Arrows fine tune it. LEFT and RIGHT snap it to a side. DONE saves it.",
                ["Placed"] = "Dropped the {0} there. Arrows fine tune it.",
                ["Snapped"] = "Snapped the {0} to the {1} side.",
                ["MoveDone"] = "UI position saved.",
                ["MoveReset"] = "UI position reset.",
                ["Help"] = "/vacuum on|off, /vacuum open [page], /vacuum move [rail|button|left|right|done|reset], /vacuum clear <page|all> confirm"
            }, this);
        }

        #endregion
    }
}

