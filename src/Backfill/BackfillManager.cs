using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Debug;
using TwitchOverlayMod.Models;
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
    private readonly List<(BackfillItem item, LocString? nameLoc, LocString? descLoc)> _pendingLoc = new();
    private readonly Queue<string>                    _metaChunks = new();
    private readonly Queue<Dictionary<int, string>>   _chunkNotes = new();
    private readonly Queue<string>                    _artChunks  = new();

    internal bool HasMetadata    => _metaChunks.Count > 0;
    internal bool HasArt         => _artChunks.Count  > 0;
    internal int  MetadataCount  => _metaChunks.Count;
    internal int  ArtCount       => _artChunks.Count;

    private bool _captureInProgress;

    internal (string? chunk, Dictionary<int, string>? notes) DequeueMetadata()
    {
        var chunk = _metaChunks.Count > 0 ? _metaChunks.Dequeue() : null;
        var notes = _chunkNotes.Count  > 0 ? _chunkNotes.Dequeue() : null;
        return (chunk, notes);
    }

    internal string? DequeueArt() =>
        _artChunks.Count > 0 ? _artChunks.Dequeue() : null;

    // Call BEFORE Load() so mappers only reflect packaged data at scan time.
    internal void Scan()
    {
        _scanned.Clear();
        _pendingLoc.Clear();
        LoadPackagedContent();
        try { ScanRelics();  } catch (Exception ex) { Logging.Log($"[Backfill] Scan relics error: {ex.Message}"); }
        try { ScanPowers();  } catch (Exception ex) { Logging.Log($"[Backfill] Scan powers error: {ex.Message}"); }
        try { ScanPotions(); } catch (Exception ex) { Logging.Log($"[Backfill] Scan potions error: {ex.Message}"); }
        try { ScanCards();   } catch (Exception ex) { Logging.Log($"[Backfill] Scan cards error: {ex.Message}"); }
        try { ScanEnemies(); } catch (Exception ex) { Logging.Log($"[Backfill] Scan enemies error: {ex.Message}"); }
        try { CollectAllTranslations(); } catch (Exception ex) { Logging.Log($"[Backfill] Loc collection error: {ex.Message}"); }
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

    internal void BuildMetadataChunks(GameStatePayload? state = null)
    {
        _metaChunks.Clear();
        _chunkNotes.Clear();

        var items = FilterActive(state);

        foreach (var group in items.GroupBy(i => i.Category))
        {
            var cat        = group.Key;
            var batch      = new List<string>();
            var batchNotes = new Dictionary<int, string>();
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
                    EnqueueChunk(cat, batch, batchNotes);
                    batch      = new List<string>();
                    batchNotes = new Dictionary<int, string>();
                    batchBytes = 0;
                }

                batch.Add(itemJson);
                if (item.ImageNote != null) batchNotes[item.Id] = item.ImageNote;
                batchBytes += itemBytes;
            }

            if (batch.Count > 0)
                EnqueueChunk(cat, batch, batchNotes);
        }

        Logging.Log($"[Backfill] Built {_metaChunks.Count} meta chunks");
    }

    internal void BuildArtChunks(GameStatePayload? state = null)
    {
        _artChunks.Clear();

        var items = FilterActive(state);

        foreach (var item in items.Where(i => i.CapturedWebP != null))
            EnqueueImageChunks(item);

        Logging.Log($"[Backfill] Built {_artChunks.Count} art chunks");
    }

    private IEnumerable<BackfillItem> FilterActive(GameStatePayload? state)
    {
        var activeIds = state != null ? CollectActiveIds(state) : null;
        return activeIds != null
            ? _scanned.Where(i => activeIds.TryGetValue(i.Category, out var catIds) && catIds.Contains(i.Id))
            : _scanned;
    }

    private void EnqueueImageChunks(BackfillItem item)
    {
        if (item.CapturedWebP == null) return;
        var b64       = Convert.ToBase64String(item.CapturedWebP);
        const int SliceSize = 4700;
        var total     = (b64.Length + SliceSize - 1) / SliceSize;
        for (int part = 1; part <= total; part++)
        {
            var start = (part - 1) * SliceSize;
            var len   = Math.Min(SliceSize, b64.Length - start);
            var json  = $"{{\"t\":\"img\",\"id\":{item.Id},\"cat\":\"{item.Category}\",\"part\":{part},\"of\":{total},\"data\":\"{b64.Substring(start, len)}\"}}";
            _artChunks.Enqueue(json);
        }
        Logging.Log($"[Backfill] Queued {total} art chunks for {item.Name} (id={item.Id})");
    }

    private void EnqueueChunk(string cat, List<string> itemJsons, Dictionary<int, string> notes)
    {
        var sb = new StringBuilder();
        sb.Append("{\"t\":\"b\",\"cat\":\"");
        sb.Append(cat);
        sb.Append("\",\"items\":[");
        sb.Append(string.Join(",", itemJsons));
        sb.Append("]}");
        _metaChunks.Enqueue(sb.ToString());
        _chunkNotes.Enqueue(notes);
    }

    private static Dictionary<string, HashSet<int>> CollectActiveIds(GameStatePayload state)
    {
        var cards   = new HashSet<int>();
        var relics  = new HashSet<int>();
        var potions = new HashSet<int>();
        var powers  = new HashSet<int>();
        var enemies = new HashSet<int>();

        if (state.Player != null)
        {
            foreach (var id in state.Player.Deck)    cards.Add(id);
            foreach (var r  in state.Player.Relics)  relics.Add(r.Id);
            foreach (var p  in state.Player.Potions) potions.Add(p.Id);
        }

        if (state.Combat != null)
        {
            foreach (var id in state.Combat.Hand)        cards.Add(id);
            foreach (var id in state.Combat.DrawPile)    cards.Add(id);
            foreach (var id in state.Combat.DiscardPile) cards.Add(id);
            foreach (var id in state.Combat.ExhaustPile) cards.Add(id);
            foreach (var pw in state.Combat.Powers)      powers.Add(pw.Id);
            foreach (var en in state.Combat.Enemies)
            {
                enemies.Add(en.Id);
                foreach (var pw in en.Powers) powers.Add(pw.Id);
            }
        }

        return new Dictionary<string, HashSet<int>>
        {
            ["cards"]   = cards,
            ["relics"]  = relics,
            ["potions"] = potions,
            ["powers"]  = powers,
            ["enemies"] = enemies
        };
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
                var item = new BackfillItem
                {
                    Category = "relics", Key = gameId, IsNew = true,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["rarity"] = rarity, ["pool"] = pool, ["image"] = (object?)null },
                    TextureGetter = () => relic.BigIcon
                };
                _pendingLoc.Add((item, relic.Title, relic.DynamicDescription));
                _scanned.Add(item);
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
                var item = new BackfillItem
                {
                    Category = "powers", Key = gameId, IsNew = true,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["type"] = type, ["stack_type"] = stack, ["image"] = (object?)null },
                    TextureGetter = () => power.BigIcon
                };
                _pendingLoc.Add((item, power.Title, power.SmartDescription));
                _scanned.Add(item);
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
                var item = new BackfillItem
                {
                    Category = "potions", Key = gameId, IsNew = true,
                    Name = liveName, Description = liveDesc,
                    ExtraFields = new() { ["game_id"] = gameId, ["rarity"] = rarity, ["pool"] = pool, ["image"] = (object?)null },
                    TextureGetter = () => potion.Image
                };
                _pendingLoc.Add((item, potion.Title, potion.DynamicDescription));
                _scanned.Add(item);
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
            var gameId = card.Id.ToString();
            var type   = card.Type.ToString();
            var rarity = card.Rarity.ToString();
            var pool   = card.Pool?.Title ?? "";

            for (int lv = 0; lv <= card.MaxUpgradeLevel; lv++)
            {
                var (title, desc, energyCost, costsX, titleLoc, descLoc) = GetCardDataAtLevel(card, lv);
                ScanCardLevel(card, gameId, lv, title, desc, type, rarity, pool, energyCost, costsX, titleLoc, descLoc);
            }
        }
    }

    private static (string title, string desc, int energyCost, bool costsX, LocString? titleLoc, LocString? descLoc)
        GetCardDataAtLevel(CardModel card, int level)
    {
        var energyCost = -1;
        var costsX     = false;
        try
        {
            var mutable = card.ToMutable();
            for (int i = 0; i < level && mutable.CurrentUpgradeLevel < mutable.MaxUpgradeLevel; i++)
            {
                mutable.UpgradeInternal();
                mutable.FinalizeUpgradeInternal();
            }
            var title = SafeText(() => mutable.Title);
            var desc  = SafeText(() => mutable.GetDescriptionForPile(PileType.None));
            try { var ec = mutable.EnergyCost; energyCost = ec.Canonical; costsX = ec.CostsX; } catch { }
            LocString? titleLoc = null;
            LocString? descLoc  = null;
            try { titleLoc = mutable.TitleLocString; } catch { }
            try { descLoc  = card.Description; } catch { }
            return (title, desc, energyCost, costsX, titleLoc, descLoc);
        }
        catch { return ("", "", energyCost, costsX, null, null); }
    }

    private void ScanCardLevel(CardModel card, string gameId, int lv,
        string liveName, string liveDesc, string type, string rarity, string pool,
        int energyCost, bool costsX, LocString? titleLoc, LocString? descLoc)
    {
        var cardKey = $"{gameId}:{lv}";
        var pkgId   = CardIdMapper.GetSequentialId(gameId, lv);

        if (pkgId == null)
        {
            var item = new BackfillItem
            {
                Category = "cards", Key = cardKey, IsNew = true,
                Name = liveName, Description = liveDesc,
                ExtraFields = new()
                {
                    ["game_id"] = gameId, ["upgrade_level"] = lv,
                    ["type"] = type, ["rarity"] = rarity, ["pool"] = pool,
                    ["energy_cost"] = energyCost, ["costs_x"] = costsX,
                    ["image"] = (object?)null
                },
            };
            _pendingLoc.Add((item, titleLoc, descLoc));
            _scanned.Add(item);
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
        if (item.IsNew)
        {
            if (item.Category == "cards")
            {
                // Art is always sent as a separate img chunk so metadata arrives first
                // and the card can render with a placeholder while art is in transit.
                item.ImageNote = item.CapturedWebP != null ? "split" : "no capture";
            }
            else if (item.TextureGetter != null)
            {
                try
                {
                    var texture = item.TextureGetter();
                    if (texture == null) { item.ImageNote = "load failed"; }
                    else
                    {
                        var image = texture.GetImage();
                        if (image == null) { item.ImageNote = "no image data"; }
                        else
                        {
                            var bytes    = image.SaveWebpToBuffer(false);
                            var b64      = Convert.ToBase64String(bytes);
                            var b64Bytes = Encoding.UTF8.GetByteCount(b64);
                            var limit    = b64Bytes > MaxImageBase64BytesBatch ? MaxImageBase64BytesSolo : MaxImageBase64BytesBatch;
                            if (b64Bytes <= limit)
                                dict["imageData"] = b64;
                            else
                                item.ImageNote = $"too large ({b64Bytes}B)";
                        }
                    }
                }
                catch (Exception ex) { item.ImageNote = $"error: {ex.Message}"; }
            }
            else
            {
                item.ImageNote = "no image";
            }
        }

        if (item.NameTranslations != null)
            dict["loc_name"] = new { table = item.NameLocTable, key = item.NameLocKey, translations = item.NameTranslations };
        if (item.DescriptionTranslations != null)
            dict["loc_description"] = new { table = item.DescriptionLocTable, key = item.DescriptionLocKey, translations = item.DescriptionTranslations };

        return JsonSerializer.Serialize(dict);
    }

    // ── Art capture ───────────────────────────────────────────────────────────

    internal void TriggerCaptureForActive(Node parent, GameStatePayload? state)
    {
        if (_captureInProgress) return;

        var activeIds = state != null ? CollectActiveIds(state) : null;
        var items = _scanned
            .Where(i => i.IsNew
                     && i.Category == "cards"
                     && i.Key.EndsWith(":0")
                     && i.CapturedWebP == null
                     && (activeIds == null
                         || (activeIds.TryGetValue(i.Category, out var catIds) && catIds.Contains(i.Id))))
            .ToList();

        if (items.Count == 0) return;

        _captureInProgress = true;
        CaptureArtAsync(parent, items, () => _captureInProgress = false);
    }

    private void CaptureArtAsync(Node parent, List<BackfillItem> items, Action onComplete)
    {
        if (items.Count == 0) { onComplete(); return; }

        ConsolePrint($"Art capture starting: {items.Count} card(s)");

        SubViewport? viewport = null;
        NCard?       card     = null;

        try
        {
            var size = new Vector2I(
                (int)(NCard.defaultSize.X + 26),
                (int)(NCard.defaultSize.Y + 26));

            viewport = new SubViewport
            {
                Size                    = size,
                TransparentBg           = true,
                RenderTargetUpdateMode  = SubViewport.UpdateMode.Always,
                RenderTargetClearMode   = SubViewport.ClearMode.Always
            };
            parent.AddChild(viewport);

            var cardScene = ResourceLoader.Load<PackedScene>("res://scenes/cards/card.tscn");
            if (cardScene == null)
            {
                viewport.QueueFree();
                onComplete();
                return;
            }

            card          = cardScene.Instantiate<NCard>();
            card.Scale    = Vector2.One;
            card.Position = new Vector2(size.X / 2f, size.Y / 2f);
            viewport.AddChild(card);
        }
        catch (Exception ex)
        {
            Logging.Log($"[Backfill] CaptureArtAsync setup error: {ex.Message}");
            viewport?.QueueFree();
            onComplete();
            return;
        }

        var queue       = new Queue<BackfillItem>(items);
        BackfillItem?   current      = null;
        int             framesToWait = 0;
        bool            initialDone  = false;
        TextureRect?    artNode      = null;
        Rect2I          artRect      = default;
        var             hiddenItems  = new List<CanvasItem>();
        Timer?          timer        = null;
        int             done         = 0;
        int             captured     = 0;
        int             currentLv    = 0;

        void SetupNext()
        {
            foreach (var ci in hiddenItems) ci.Visible = true;
            hiddenItems.Clear();
            artNode     = null;
            artRect     = default;
            initialDone = false;

            if (queue.Count == 0)
            {
                timer?.QueueFree();
                viewport!.QueueFree();
                Logging.Log($"[Backfill] Art capture done: {captured}/{items.Count} captured");
                ConsolePrint($"Art capture done: {captured}/{items.Count} captured");
                onComplete();
                return;
            }

            current = queue.Dequeue();

            var parts = current.Key.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var lv))
            {
                current.ImageNote = "bad key";
                ConsolePrint($"Skip {done + 1}/{items.Count}: {current.Key} (bad key)");
                done++;
                SetupNext();
                return;
            }

            currentLv = lv;
            var gameId    = parts[0];
            var cardModel = ModelDb.AllCards.FirstOrDefault(c => c.Id.ToString() == gameId);
            if (cardModel == null)
            {
                current.ImageNote = "model not found";
                ConsolePrint($"Skip {done + 1}/{items.Count}: {gameId} (model not found)");
                done++;
                SetupNext();
                return;
            }

            ConsolePrint($"Capturing {done + 1}/{items.Count}: {cardModel.Title} ({gameId})");

            try
            {
                var mutable = cardModel.ToMutable();
                for (int i = 0; i < lv && mutable.CurrentUpgradeLevel < mutable.MaxUpgradeLevel; i++)
                {
                    mutable.UpgradeInternal();
                    mutable.FinalizeUpgradeInternal();
                }
                card!.Model = mutable;
                card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
            }
            catch (Exception ex)
            {
                current.ImageNote = $"setup error: {ex.Message}";
                done++;
                SetupNext();
                return;
            }

            framesToWait = 2;
        }

        void OnTick()
        {
            try
            {
                if (framesToWait > 0) { framesToWait--; return; }
                if (current == null) { SetupNext(); return; }

                if (!initialDone)
                {
                    // Measure art rect before hiding anything.
                    artNode = FindArtNode(card!);
                    artRect = artNode != null ? GetGlobalRectI(artNode) : default;

                    // Hide all CanvasItem descendants except the art node and its ancestors.
                    var keepVisible = new HashSet<Node>();
                    if (artNode != null)
                    {
                        Node? n = artNode;
                        while (n != null) { keepVisible.Add(n); if (n == card) break; n = n.GetParent(); }
                    }
                    foreach (var ci in FindDescendants<CanvasItem>(card!))
                    {
                        if (!keepVisible.Contains(ci) && ci.Visible)
                        { ci.Visible = false; hiddenItems.Add(ci); }
                    }
                    if (artNode != null) artNode.Visible = true;

                    initialDone  = true;
                    framesToWait = 2;
                }
                else
                {
                    // Capture the art-only render.
                    try
                    {
                        if (artRect.Size.X > 0 && artRect.Size.Y > 0)
                        {
                            var full  = viewport!.GetTexture().GetImage();
                            var iw    = full.GetWidth();
                            var ih    = full.GetHeight();
                            var rx    = Math.Clamp(artRect.Position.X, 0, iw - 1);
                            var ry    = Math.Clamp(artRect.Position.Y, 0, ih - 1);
                            var rw    = Math.Clamp(artRect.Size.X,     1, iw - rx);
                            var rh    = Math.Clamp(artRect.Size.Y,     1, ih - ry);
                            var img   = full.GetRegion(new Rect2I(rx, ry, rw, rh));
                            const int MaxDim = 120;
                            if (img.GetWidth() > MaxDim || img.GetHeight() > MaxDim)
                            {
                                var scale = Math.Min((float)MaxDim / img.GetWidth(), (float)MaxDim / img.GetHeight());
                                img.Resize((int)(img.GetWidth() * scale), (int)(img.GetHeight() * scale), Image.Interpolation.Bilinear);
                            }
                            current!.CapturedWebP = img.SaveWebpToBuffer(true);

                            // Share the same art bytes with every upgrade level of this card.
                            var baseGameId = current.Key.Substring(0, current.Key.LastIndexOf(':'));
                            foreach (var sibling in _scanned)
                            {
                                if (sibling != current &&
                                    sibling.IsNew &&
                                    sibling.Category == "cards" &&
                                    sibling.Key.StartsWith(baseGameId + ":"))
                                    sibling.CapturedWebP = current.CapturedWebP;
                            }
                        }
                        else
                        {
                            current!.ImageNote = "art rect not found";
                        }
                    }
                    catch (Exception ex)
                    {
                        current!.ImageNote = $"capture error: {ex.Message}";
                    }

                    done++;
                    if (current!.CapturedWebP != null) captured++;
                    SetupNext();
                }
            }
            catch (Exception ex)
            {
                Logging.Log($"[Backfill] Capture tick error: {ex.Message}");
                if (current != null)
                {
                    current.ImageNote = $"tick error: {ex.Message}";
                    done++;
                }
                SetupNext();
            }
        }

        timer = new Timer { WaitTime = 0.1, Autostart = true };
        timer.Connect(Timer.SignalName.Timeout, Callable.From(OnTick));
        parent.AddChild(timer);
        SetupNext();
    }

    // ── Node-finding helpers (mirrored from CardExporter) ─────────────────────

    private static TextureRect? FindArtNode(Node root)
    {
        var names = new[] {
            "Art", "CardArt", "ArtRect", "Portrait", "Artwork",
            "ArtArea", "ArtImage", "CardImage", "Illustration", "ArtContainer"
        };
        foreach (var name in names)
        {
            if (FindDescendantByName<TextureRect>(root, name) is { } found && found.Texture != null)
                return found;
        }
        if (FindDescendantWhere<TextureRect>(root,
            n => n.Name.ToString().IndexOf("art", StringComparison.OrdinalIgnoreCase) >= 0
              && n.Texture != null) is { } match)
            return match;
        return FindLargestTextureRect(root);
    }

    private static Rect2I GetGlobalRectI(Control node)
    {
        try
        {
            var r = node.GetGlobalRect();
            return new Rect2I((int)r.Position.X, (int)r.Position.Y, (int)r.Size.X, (int)r.Size.Y);
        }
        catch { return default; }
    }

    private static T? FindDescendantByName<T>(Node root, string name) where T : Node
    {
        var q = new Queue<Node>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            if (n != root && n.Name == name && n is T m) return m;
            foreach (var c in n.GetChildren()) q.Enqueue(c);
        }
        return null;
    }

    private static T? FindDescendantWhere<T>(Node root, Func<T, bool> pred) where T : Node
    {
        var q = new Queue<Node>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            if (n != root && n is T m && pred(m)) return m;
            foreach (var c in n.GetChildren()) q.Enqueue(c);
        }
        return null;
    }

    private static List<T> FindDescendants<T>(Node root) where T : Node
    {
        var result = new List<T>();
        var q      = new Queue<Node>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var n = q.Dequeue();
            if (n != root && n is T m) result.Add(m);
            foreach (var c in n.GetChildren()) q.Enqueue(c);
        }
        return result;
    }

    private static TextureRect? FindLargestTextureRect(Node root)
    {
        TextureRect? best     = null;
        float        bestArea = 0;
        foreach (var tr in FindDescendants<TextureRect>(root))
        {
            if (tr.Texture == null) continue;
            var area = tr.Size.X * tr.Size.Y;
            if (area > bestArea) { bestArea = area; best = tr; }
        }
        return best;
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

    // Sweeps all languages once, blocking LocManager signals during the loop so no
    // deferred language-change events queue up. Signals are re-enabled before the final
    // restore call, so exactly one language-change signal fires (for the original lang).
    private void CollectAllTranslations()
    {
        if (_pendingLoc.Count == 0) return;

        var nameMap = new Dictionary<(string table, string key), List<BackfillItem>>();
        var descMap = new Dictionary<(string table, string key), List<BackfillItem>>();

        foreach (var (item, nameLoc, descLoc) in _pendingLoc)
        {
            if (nameLoc != null)
            {
                item.NameLocTable = nameLoc.LocTable;
                item.NameLocKey   = nameLoc.LocEntryKey;
                var k = (nameLoc.LocTable, nameLoc.LocEntryKey);
                if (!nameMap.ContainsKey(k)) nameMap[k] = new();
                nameMap[k].Add(item);
            }
            if (descLoc != null)
            {
                item.DescriptionLocTable = descLoc.LocTable;
                item.DescriptionLocKey   = descLoc.LocEntryKey;
                var k = (descLoc.LocTable, descLoc.LocEntryKey);
                if (!descMap.ContainsKey(k)) descMap[k] = new();
                descMap[k].Add(item);
            }
        }

        var originalLang = "eng";
        try { originalLang = SaveManager.Instance?.SettingsSave?.Language ?? "eng"; } catch { }
        try
        {
            foreach (var lang in LocManager.Languages)
            {
                try
                {
                    LocManager.Instance.SetLanguage(lang);

                    foreach (var ((table, key), items) in nameMap)
                    {
                        try
                        {
                            var t = LocManager.Instance.GetTable(table);
                            if (!t.HasEntry(key)) continue;
                            var text = t.GetRawText(key);
                            foreach (var item in items)
                            {
                                item.NameTranslations ??= new();
                                item.NameTranslations[lang] = text;
                            }
                        }
                        catch { }
                    }

                    foreach (var ((table, key), items) in descMap)
                    {
                        try
                        {
                            var t = LocManager.Instance.GetTable(table);
                            if (!t.HasEntry(key)) continue;
                            var text = t.GetRawText(key);
                            foreach (var item in items)
                            {
                                item.DescriptionTranslations ??= new();
                                item.DescriptionTranslations[lang] = text;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
        finally
        {
            try { LocManager.Instance.SetLanguage(originalLang); } catch { }
        }
    }

    private static void ConsolePrint(string message)
    {
        try
        {
            var buf = NDevConsole.Instance.GetNode<RichTextLabel>("OutputContainer/OutputBuffer");
            buf.Text += $"[TwitchOverlay] {message}\n";
        }
        catch { }
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
    // Raw WebP bytes captured via SubViewport; cards only, set by CaptureArtAsync.
    public byte[]? CapturedWebP { get; set; }
    // Set by SerializeItem for new items: null = image included, otherwise the skip reason.
    public string? ImageNote { get; set; }
    // Localization data collected at scan time for new (modded) items.
    public string? NameLocTable               { get; set; }
    public string? NameLocKey                 { get; set; }
    public Dictionary<string, string>? NameTranslations        { get; set; }
    public string? DescriptionLocTable        { get; set; }
    public string? DescriptionLocKey          { get; set; }
    public Dictionary<string, string>? DescriptionTranslations { get; set; }
}
