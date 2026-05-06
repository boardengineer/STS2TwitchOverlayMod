using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using TwitchOverlayMod.State;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.Backfill;

internal class BackfillManager
{
    private const int MaxChunkBytes = 4800;
    // For missing items we try to include imageData; omit only if the encoded image
    // alone would push a solo chunk over the PubSub 5 120-byte hard limit.
    private const int MaxImageBase64BytesSolo = 4900;
    // For items that share a chunk with others keep images small so batching works.
    private const int MaxImageBase64BytesBatch = 3072;

    private static readonly string CachePath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "TwitchOverlayMod_backfill_cache.json");

    // ── Packaged data for diff ────────────────────────────────────────────────

    // Loaded from embedded resources; keyed by the same lookup key used by each mapper.
    private Dictionary<string, PackagedEntry> _pkgRelics  = new();
    private Dictionary<string, PackagedEntry> _pkgPowers  = new();
    private Dictionary<string, PackagedEntry> _pkgPotions = new();
    private Dictionary<string, PackagedEntry> _pkgCards   = new(); // key = "game_id:upgrade_level"
    private Dictionary<string, PackagedEntry> _pkgEnemies = new(); // key = game_id (monster.Id.Entry)
    private Dictionary<string, PackagedEntry> _pkgIntents = new(); // key = intent_type

    // ── State ─────────────────────────────────────────────────────────────────

    private Dictionary<string, Dictionary<string, int>> _cache = new();
    private readonly List<BackfillItem> _scanned = new();
    private readonly Queue<string> _chunks = new();

    internal bool HasChunks => _chunks.Count > 0;

    internal string? DequeueChunk()
        => _chunks.Count > 0 ? _chunks.Dequeue() : null;

    // Call BEFORE Load() so mappers only reflect packaged data at scan time.
    internal void Scan()
    {
        _scanned.Clear();
        LoadPackagedContent();
        try { ScanRelics();  } catch (Exception ex) { Logging.Log($"[Backfill] Scan relics error: {ex.Message}"); }
        try { ScanPowers();  } catch (Exception ex) { Logging.Log($"[Backfill] Scan powers error: {ex.Message}"); }
        try { ScanPotions(); } catch (Exception ex) { Logging.Log($"[Backfill] Scan potions error: {ex.Message}"); }
        try { ScanCards();   } catch (Exception ex) { Logging.Log($"[Backfill] Scan cards error: {ex.Message}"); }
        try { ScanEnemies(); } catch (Exception ex) { Logging.Log($"[Backfill] Scan enemies error: {ex.Message}"); }
        var newCount     = _scanned.Count(i => i.IsNew);
        var changedCount = _scanned.Count(i => !i.IsNew);
        Logging.Log($"[Backfill] Scan: {newCount} new, {changedCount} changed");
    }

    internal void Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json)
                     ?? new Dictionary<string, Dictionary<string, int>>();
            Logging.Log($"[Backfill] Cache loaded: {_cache.Values.Sum(d => d.Count)} entries");
        }
        catch (Exception ex)
        {
            Logging.Log($"[Backfill] Cache load error: {ex.Message}");
            _cache = new Dictionary<string, Dictionary<string, int>>();
        }
    }

    internal void AssignIds()
    {
        var dirty = false;

        foreach (var item in _scanned)
        {
            // Changed items already carry the packaged ID — no allocation needed.
            if (!item.IsNew) continue;

            if (!_cache.TryGetValue(item.Category, out var catMap))
            {
                catMap = new Dictionary<string, int>();
                _cache[item.Category] = catMap;
            }

            if (catMap.TryGetValue(item.Key, out var existing))
            {
                item.Id = existing;
            }
            else
            {
                var newId = NextId(item.Category);
                catMap[item.Key] = newId;
                item.Id = newId;
                dirty = true;
                Logging.Log($"[Backfill] Assigned {item.Category}/{item.Key} → {newId}");
            }
        }

        // Register all new items with their mappers so GameStateCollector can resolve them.
        foreach (var item in _scanned.Where(i => i.IsNew))
            RegisterWithMapper(item);

        if (dirty) SaveCache();
    }

    internal void BuildChunks()
    {
        _chunks.Clear();

        foreach (var group in _scanned.GroupBy(i => i.Category))
        {
            var cat = group.Key;
            var batch = new List<string>();
            var batchBytes = 0;

            foreach (var item in group)
            {
                string itemJson;
                try { itemJson = SerializeItem(item); }
                catch (Exception ex)
                {
                    Logging.Log($"[Backfill] Serialize error for {cat}/{item.Key}: {ex.Message}");
                    continue;
                }

                var itemBytes = Encoding.UTF8.GetByteCount(itemJson);

                if (batch.Count > 0 && batchBytes + itemBytes + 20 > MaxChunkBytes)
                {
                    EnqueueChunk(cat, batch);
                    batch = new List<string>();
                    batchBytes = 0;
                }

                batch.Add(itemJson);
                batchBytes += itemBytes;
            }

            if (batch.Count > 0)
                EnqueueChunk(cat, batch);
        }

        Logging.Log($"[Backfill] Built {_chunks.Count} chunks for {_scanned.Count} items");
    }

    private void EnqueueChunk(string cat, List<string> itemJsons)
    {
        var sb = new StringBuilder();
        sb.Append("{\"t\":\"b\",\"cat\":\"");
        sb.Append(cat);
        sb.Append("\",\"items\":[");
        sb.Append(string.Join(",", itemJsons));
        sb.Append("]}");
        _chunks.Enqueue(sb.ToString());
    }

    // ── Packaged data loading ─────────────────────────────────────────────────

    private void LoadPackagedContent()
    {
        _pkgRelics  = LoadPackaged("TwitchOverlayMod.data.relics.json",
            e => e.GameId ?? "");
        _pkgPowers  = LoadPackaged("TwitchOverlayMod.data.powers.json",
            e => e.GameId ?? "");
        _pkgPotions = LoadPackaged("TwitchOverlayMod.data.potions.json",
            e => e.GameId ?? "");
        _pkgIntents = LoadPackaged("TwitchOverlayMod.data.intents.json",
            e => e.IntentType ?? "");
        _pkgEnemies = LoadPackaged("TwitchOverlayMod.data.enemies.json",
            e => e.GameId ?? "");
        _pkgCards   = LoadPackaged("TwitchOverlayMod.data.cards.json",
            e => $"{e.GameId}:{e.UpgradeLevel}");
    }

    private static Dictionary<string, PackagedEntry> LoadPackaged(
        string resource, Func<PackagedEntry, string> keySelector)
    {
        var dict = new Dictionary<string, PackagedEntry>();
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(resource);
            if (stream == null) return dict;
            using var reader = new StreamReader(stream);
            var entries = JsonSerializer.Deserialize<List<PackagedEntry>>(reader.ReadToEnd());
            if (entries == null) return dict;
            foreach (var e in entries)
            {
                var key = keySelector(e);
                if (!string.IsNullOrEmpty(key))
                    dict.TryAdd(key, e);
            }
        }
        catch (Exception ex)
        {
            Logging.Log($"[Backfill] LoadPackaged({resource}) error: {ex.Message}");
        }
        return dict;
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    private void ScanRelics()
    {
        foreach (var relic in ModelDb.AllRelics)
        {
            var gameId   = relic.Id.ToString();
            var liveName = SafeText(() => relic.Title.GetFormattedText());
            var liveDesc = SafeText(() => relic.DynamicDescription.GetFormattedText());
            var rarity   = relic.Rarity.ToString();
            var pool     = GetRelicPool(relic);

            var pkgId = RelicIdMapper.GetSequentialId(gameId);
            if (pkgId == null)
            {
                // New item
                _scanned.Add(new BackfillItem
                {
                    Category = "relics", Key = gameId, IsNew = true,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["rarity"] = rarity, ["pool"] = pool, ["image"] = (object?)null },
                    TextureGetter = () => relic.BigIcon
                });
            }
            else if (_pkgRelics.TryGetValue(gameId, out var pkg) && ContentChanged(pkg, liveName, liveDesc))
            {
                // Existing item with updated text
                _scanned.Add(new BackfillItem
                {
                    Category = "relics", Key = gameId, IsNew = false,
                    Id = pkgId.Value,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["rarity"] = rarity, ["pool"] = pool, ["image"] = pkg.Image }
                });
            }
        }
    }

    private void ScanPowers()
    {
        foreach (var power in ModelDb.AllPowers)
        {
            var gameId   = power.Id.Entry;
            var liveName = SafeText(() => power.Title.GetFormattedText());
            var liveDesc = SafeText(() => power.SmartDescription.GetRawText());
            var type     = power.Type.ToString();
            var stack    = power.StackType.ToString();

            var pkgId = PowerIdMapper.GetSequentialId(gameId);
            if (pkgId == null)
            {
                _scanned.Add(new BackfillItem
                {
                    Category = "powers", Key = gameId, IsNew = true,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["type"] = type, ["stack_type"] = stack, ["image"] = (object?)null },
                    TextureGetter = () => power.BigIcon
                });
            }
            else if (_pkgPowers.TryGetValue(gameId, out var pkg) && ContentChanged(pkg, liveName, liveDesc))
            {
                _scanned.Add(new BackfillItem
                {
                    Category = "powers", Key = gameId, IsNew = false,
                    Id = pkgId.Value,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["type"] = type, ["stack_type"] = stack, ["image"] = pkg.Image }
                });
            }
        }
    }

    private void ScanPotions()
    {
        foreach (var potion in ModelDb.AllPotions)
        {
            var gameId   = potion.Id.Entry;
            var liveName = SafeText(() => potion.Title.GetFormattedText());
            var liveDesc = SafeText(() => potion.DynamicDescription.GetFormattedText());
            var rarity   = potion.Rarity.ToString();
            var pool     = GetPotionPool(potion);

            var pkgId = PotionIdMapper.GetSequentialId(gameId);
            if (pkgId == null)
            {
                _scanned.Add(new BackfillItem
                {
                    Category = "potions", Key = gameId, IsNew = true,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["rarity"] = rarity, ["pool"] = pool, ["image"] = (object?)null },
                    TextureGetter = () => potion.Image
                });
            }
            else if (_pkgPotions.TryGetValue(gameId, out var pkg) && ContentChanged(pkg, liveName, liveDesc))
            {
                _scanned.Add(new BackfillItem
                {
                    Category = "potions", Key = gameId, IsNew = false,
                    Id = pkgId.Value,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["rarity"] = rarity, ["pool"] = pool, ["image"] = pkg.Image }
                });
            }
        }
    }

    private void ScanCards()
    {
        foreach (var card in ModelDb.AllCards)
        {
            var gameId    = card.Id.ToString();
            var baseTitle = SafeText(() => card.TitleLocString.GetFormattedText());
            var desc      = SafeText(() => card.Description.GetFormattedText());
            var type      = card.Type.ToString();
            var rarity    = card.Rarity.ToString();
            var pool      = card.Pool?.Title ?? "";

            ScanCardLevel(card, gameId, 0, baseTitle, desc, type, rarity, pool);
            for (int lv = 1; lv <= card.MaxUpgradeLevel; lv++)
            {
                var upgTitle = card.MaxUpgradeLevel > 1 ? $"{baseTitle}+{lv}" : $"{baseTitle}+";
                ScanCardLevel(card, gameId, lv, upgTitle, desc, type, rarity, pool);
            }
        }
    }

    private void ScanCardLevel(CardModel card, string gameId, int lv,
        string liveName, string liveDesc, string type, string rarity, string pool)
    {
        var cardKey = $"{gameId}:{lv}";
        var pkgId   = CardIdMapper.GetSequentialId(gameId, lv);

        if (pkgId == null)
        {
            _scanned.Add(new BackfillItem
            {
                Category = "cards", Key = cardKey, IsNew = true,
                Name = liveName, Description = liveDesc,
                ExtraFields = new()
                {
                    ["game_id"] = gameId, ["upgrade_level"] = lv,
                    ["type"] = type, ["rarity"] = rarity, ["pool"] = pool,
                    ["image"] = (object?)null
                },
                TextureGetter = MakeCardPortraitGetter(card)
            });
        }
        else if (_pkgCards.TryGetValue(cardKey, out var pkg) && ContentChanged(pkg, liveName, liveDesc))
        {
            _scanned.Add(new BackfillItem
            {
                Category = "cards", Key = cardKey, IsNew = false,
                Id = pkgId.Value,
                Name = liveName, Description = liveDesc,
                ExtraFields = new()
                {
                    ["game_id"] = gameId, ["upgrade_level"] = lv,
                    ["type"] = type, ["rarity"] = rarity, ["pool"] = pool,
                    ["image"] = pkg.Image
                }
            });
        }
    }

    private void ScanEnemies()
    {
        foreach (var monster in ModelDb.Monsters.Distinct())
        {
            var gameId   = monster.Id.Entry;
            var name     = SafeText(() => monster.Title.GetFormattedText());
            var pkgId    = EnemyIdMapper.GetSequentialId(name);

            if (pkgId == null)
            {
                _scanned.Add(new BackfillItem
                {
                    Category = "enemies", Key = name, IsNew = true,
                    Name = name,
                    ExtraFields = new()
                    {
                        ["game_id"] = gameId, ["name"] = name,
                        ["min_hp"] = monster.MinInitialHp, ["max_hp"] = monster.MaxInitialHp,
                        ["image"] = (object?)null
                    }
                });
            }
            else if (_pkgEnemies.TryGetValue(gameId, out var pkg) && pkg.Name != name)
            {
                // Enemy display name changed (e.g., localization update)
                _scanned.Add(new BackfillItem
                {
                    Category = "enemies", Key = name, IsNew = false,
                    Id = pkgId.Value,
                    Name = name,
                    ExtraFields = new()
                    {
                        ["game_id"] = gameId, ["name"] = name,
                        ["min_hp"] = monster.MinInitialHp, ["max_hp"] = monster.MaxInitialHp,
                        ["image"] = pkg.Image
                    }
                });
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool ContentChanged(PackagedEntry pkg, string liveName, string liveDesc)
    {
        var pkgName = pkg.Name ?? pkg.Title ?? "";
        var pkgDesc = pkg.Description ?? "";
        return pkgName != liveName || pkgDesc != liveDesc;
    }

    // Returns a getter for the card's packed PNG portrait, or null if unavailable.
    private static Func<Texture2D?>? MakeCardPortraitGetter(CardModel card)
    {
        try
        {
            var pool  = card.Pool?.Title.ToLowerInvariant() ?? "";
            var entry = card.Id.Entry.ToLowerInvariant();
            var rel   = $"packed/card_portraits/{pool}/{entry}.png";
            var path  = ImageHelper.GetImagePath(rel);
            if (!ResourceLoader.Exists(path)) return null;
            return () =>
            {
                try { return ResourceLoader.Load<Texture2D>(path, null, ResourceLoader.CacheMode.Reuse); }
                catch { return null; }
            };
        }
        catch { return null; }
    }

    private int NextId(string category)
    {
        var floor = PackagedMaxId(category);
        if (!_cache.TryGetValue(category, out var catMap) || catMap.Count == 0)
            return floor + 1;
        return Math.Max(catMap.Values.Max(), floor) + 1;
    }

    private static int PackagedMaxId(string category) => category switch
    {
        "relics"  => 288,
        "powers"  => 269,
        "potions" => 63,
        "intents" => 16,
        "enemies" => 101,
        "cards"   => 1116,
        _         => 0
    };

    private void RegisterWithMapper(BackfillItem item)
    {
        try
        {
            switch (item.Category)
            {
                case "relics":  RelicIdMapper.Register(item.Key, item.Id);  break;
                case "powers":  PowerIdMapper.Register(item.Key, item.Id);  break;
                case "potions": PotionIdMapper.Register(item.Key, item.Id); break;
                case "enemies": EnemyIdMapper.Register(item.Key, item.Id);  break;
                case "cards":
                    var parts = item.Key.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var lv))
                        CardIdMapper.Register(parts[0], lv, item.Id);
                    break;
            }
        }
        catch { }
    }

    private string SerializeItem(BackfillItem item)
    {
        var dict = new Dictionary<string, object?>(item.ExtraFields)
        {
            ["id"] = item.Id
        };

        if (!dict.ContainsKey("name"))        dict["name"]        = item.Name;
        if (!dict.ContainsKey("description")) dict["description"] = item.Description;

        // Include imageData only for brand-new items (missing from packaged data).
        // Changed items keep their existing packaged image path — no re-encoding needed.
        if (item.IsNew && item.TextureGetter != null)
        {
            try
            {
                var texture = item.TextureGetter();
                if (texture != null)
                {
                    var image = texture.GetImage();
                    if (image != null)
                    {
                        var bytes    = image.SaveWebpToBuffer(false);
                        var b64      = Convert.ToBase64String(bytes);
                        var b64Bytes = Encoding.UTF8.GetByteCount(b64);

                        // For a solo chunk the encoded image may be larger; accept up to the
                        // PubSub ceiling minus wrapper overhead. For batched items keep it tight.
                        var limit = MaxImageBase64BytesBatch;
                        if (b64Bytes > limit)
                        {
                            // Re-check: will this item be the only one in its chunk?
                            // We can't know for certain at serialize time, so use the solo limit.
                            limit = MaxImageBase64BytesSolo;
                        }

                        if (b64Bytes <= limit)
                            dict["imageData"] = b64;
                    }
                }
            }
            catch { }
        }

        return JsonSerializer.Serialize(dict);
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath,
                JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logging.Log($"[Backfill] Cache save error: {ex.Message}");
        }
    }

    private static string SafeText(Func<string> getter)
    {
        try { return getter(); } catch { return ""; }
    }

    private static string GetRelicPool(RelicModel relic)
    {
        var entry = (relic.Pool?.Id.Entry ?? "unknown").ToLowerInvariant();
        return entry switch
        {
            "defect_relic_pool"      => "defect",
            "deprecated_relic_pool"  => "deprecated",
            "event_relic_pool"       => "event",
            "fallback_relic_pool"    => "fallback",
            "ironclad_relic_pool"    => "ironclad",
            "necrobinder_relic_pool" => "necrobinder",
            "regent_relic_pool"      => "regent",
            "shared_relic_pool"      => "shared",
            "silent_relic_pool"      => "silent",
            _                        => entry
        };
    }

    private static string GetPotionPool(PotionModel potion)
    {
        var entry = (potion.Pool?.Id.Entry ?? "unknown").ToLowerInvariant();
        return entry switch
        {
            "defect_potion_pool"     => "defect",
            "deprecated_potion_pool" => "deprecated",
            "event_potion_pool"      => "event",
            "ironclad_potion_pool"   => "ironclad",
            "necrobinder_potion_pool" => "necrobinder",
            "regent_potion_pool"     => "regent",
            "shared_potion_pool"     => "shared",
            "silent_potion_pool"     => "silent",
            "token_potion_pool"      => "token",
            _                        => entry
        };
    }
}

// Minimal projection of a packaged JSON entry used for content diffing.
internal class PackagedEntry
{
    [JsonPropertyName("id")]           public int    Id          { get; set; }
    [JsonPropertyName("game_id")]      public string? GameId     { get; set; }
    [JsonPropertyName("intent_type")]  public string? IntentType { get; set; }
    [JsonPropertyName("upgrade_level")] public int   UpgradeLevel { get; set; }
    [JsonPropertyName("name")]         public string? Name       { get; set; }
    [JsonPropertyName("title")]        public string? Title      { get; set; }
    [JsonPropertyName("description")]  public string? Description { get; set; }
    [JsonPropertyName("image")]        public string? Image      { get; set; }
}

internal class BackfillItem
{
    public string Category { get; set; } = "";
    public string Key      { get; set; } = "";
    public int    Id       { get; set; }
    // true  = item is absent from the packaged data → new sequential ID required
    // false = item has a packaged ID but title/description have changed
    public bool   IsNew    { get; set; } = true;
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, object?> ExtraFields { get; set; } = new();
    // Null for changed items (image unchanged) or categories without extractable images.
    public Func<Texture2D?>? TextureGetter { get; set; }
}
