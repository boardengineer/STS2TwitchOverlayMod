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
using MegaCrit.Sts2.Core.Runs;
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
    private Dictionary<string, PackagedEntry> _pkgRelics       = new();
    private Dictionary<string, PackagedEntry> _pkgPowers       = new();
    private Dictionary<string, PackagedEntry> _pkgPotions      = new();
    private Dictionary<string, PackagedEntry> _pkgCards        = new(); // key = "game_id:upgrade_level"
    private Dictionary<string, PackagedEntry> _pkgEnemies      = new(); // key = game_id (monster.Id.Entry)
    private Dictionary<string, PackagedEntry> _pkgIntents      = new(); // key = intent_type
    private Dictionary<string, PackagedEntry> _pkgEnchantments = new();

    // ── State ─────────────────────────────────────────────────────────────────

    private Dictionary<string, Dictionary<string, int>> _cache = new();
    private readonly List<BackfillItem> _scanned = new();
    private readonly List<(BackfillItem item, LocString? nameLoc, LocString? descLoc)> _pendingLoc = new();
    private readonly Queue<string>                       _metaChunks  = new();
    private readonly Queue<Dictionary<int, string>>      _chunkNotes  = new();
    private readonly Queue<string>                       _metaLabels  = new();
    // art/large queues store (json, label) so the scheduler can log what's being sent.
    private readonly Queue<(string json, string label)>  _artChunks   = new();
    // Large images (frames, map pointers) share a separate queue so they don't
    // starve the small-art queue (relic/power/potion icons, card art).
    private readonly Queue<(string json, string label)>  _largeChunks = new();

    internal bool HasMetadata    => _metaChunks.Count  > 0;
    internal bool HasArt         => _artChunks.Count   > 0;
    internal bool HasLarge       => _largeChunks.Count > 0;
    internal int  MetadataCount  => _metaChunks.Count;
    internal int  ArtCount       => _artChunks.Count;
    internal int  LargeCount     => _largeChunks.Count;

    private bool _captureInProgress;

    private readonly Dictionary<string, FrameCapture> _capturedFrames   = new();
    private readonly Dictionary<string, byte[]>       _capturedPointers = new();

    internal (string? chunk, Dictionary<int, string>? notes, string? label) DequeueMetadata()
    {
        var chunk = _metaChunks.Count > 0 ? _metaChunks.Dequeue() : null;
        var notes = _chunkNotes.Count > 0 ? _chunkNotes.Dequeue() : null;
        var label = _metaLabels.Count > 0 ? _metaLabels.Dequeue() : null;
        return (chunk, notes, label);
    }

    internal (string? json, string? label) DequeueArt()   => _artChunks.Count   > 0 ? _artChunks.Dequeue()   : (null, null);
    internal (string? json, string? label) DequeueLarge() => _largeChunks.Count > 0 ? _largeChunks.Dequeue() : (null, null);

    // Call BEFORE Load() so mappers only reflect packaged data at scan time.
    internal void Scan()
    {
        _scanned.Clear();
        _pendingLoc.Clear();
        LoadPackagedContent();
        try { ScanRelics();       } catch (Exception ex) { Logging.Log($"[Backfill] Scan relics error: {ex.Message}"); }
        try { ScanPowers();       } catch (Exception ex) { Logging.Log($"[Backfill] Scan powers error: {ex.Message}"); }
        try { ScanPotions();      } catch (Exception ex) { Logging.Log($"[Backfill] Scan potions error: {ex.Message}"); }
        try { ScanCards();        } catch (Exception ex) { Logging.Log($"[Backfill] Scan cards error: {ex.Message}"); }
        try { ScanEnemies();      } catch (Exception ex) { Logging.Log($"[Backfill] Scan enemies error: {ex.Message}"); }
        try { ScanEnchantments(); } catch (Exception ex) { Logging.Log($"[Backfill] Scan enchantments error: {ex.Message}"); }
        try { CollectAllTranslations(); } catch (Exception ex) { Logging.Log($"[Backfill] Loc collection error: {ex.Message}"); }
        try { CaptureNonCardImages(); } catch (Exception ex) { Logging.Log($"[Backfill] Non-card image capture: {ex.Message}"); }
        try { CapturePoolPointers();  } catch (Exception ex) { Logging.Log($"[Backfill] Pool pointer capture: {ex.Message}"); }
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
        foreach (var item in FilterActive(state).Where(i => i.CapturedWebP != null))
            EnqueueImageChunks(item);
        Logging.Log($"[Backfill] Built {_artChunks.Count} art chunks");
    }

    internal void BuildLargeChunks(GameStatePayload? state = null)
    {
        _largeChunks.Clear();

        var neededFrames = CollectNeededFrameKeys(state);
        foreach (var (key, fc) in _capturedFrames)
        {
            if (neededFrames.Contains(key))
                EnqueueFrameChunk(key, fc);
        }

        foreach (var (charId, webp) in _capturedPointers)
        {
            Logging.Log($"[Backfill] Enqueueing pointer for {charId} ({webp.Length}b)");
            EnqueuePointerChunk(charId, webp);
        }

        Logging.Log($"[Backfill] Built {_largeChunks.Count} large chunks ({neededFrames.Count} frame key(s), {_capturedPointers.Count} pointer(s))");
    }

    // Returns the set of frame keys used by cards that are currently active in the
    // game state. Returns an empty set when no run is active (no chunks sent).
    private HashSet<string> CollectNeededFrameKeys(GameStatePayload? state)
    {
        var keys = new HashSet<string>();
        if (state == null) return keys;

        var activeIds = CollectActiveIds(state);
        if (!activeIds.TryGetValue("cards", out var cardIds) || cardIds.Count == 0)
            return keys;

        foreach (var item in _scanned)
        {
            if (item.Category != "cards") continue;
            if (!cardIds.Contains(item.Id)) continue;
            var fk = GetFrameKey(item);
            if (_capturedFrames.ContainsKey(fk))
                keys.Add(fk);
        }
        return keys;
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
            _artChunks.Enqueue((json, $"{item.Category}/{item.Id} ({item.Name}) p{part}/{total}"));
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
        _metaLabels.Enqueue($"{cat} ({itemJsons.Count} items)");
    }

    private static Dictionary<string, HashSet<int>> CollectActiveIds(GameStatePayload state)
    {
        var cards        = new HashSet<int>();
        var relics       = new HashSet<int>();
        var potions      = new HashSet<int>();
        var powers       = new HashSet<int>();
        var enemies      = new HashSet<int>();
        var enchantments = new HashSet<int>();

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

        if (state.Event != null)
        {
            foreach (var opt in state.Event.Options)
            foreach (var tip in opt.HoverTips)
            {
                if (tip.Type == "enchantment")
                {
                    var seqId = EnchantmentIdMapper.GetSequentialId(tip.Id);
                    if (seqId.HasValue) enchantments.Add(seqId.Value);
                }
            }
        }

        return new Dictionary<string, HashSet<int>>
        {
            ["cards"]        = cards,
            ["relics"]       = relics,
            ["potions"]      = potions,
            ["powers"]       = powers,
            ["enemies"]      = enemies,
            ["enchantments"] = enchantments
        };
    }

    // ── Packaged data loading ─────────────────────────────────────────────────

    private void LoadPackagedContent()
    {
        _pkgRelics        = LoadPackaged("TwitchOverlayMod.data.relics.json",
            e => e.GameId ?? "");
        _pkgPowers        = LoadPackaged("TwitchOverlayMod.data.powers.json",
            e => e.GameId ?? "");
        _pkgPotions       = LoadPackaged("TwitchOverlayMod.data.potions.json",
            e => e.GameId ?? "");
        _pkgIntents       = LoadPackaged("TwitchOverlayMod.data.intents.json",
            e => e.IntentType ?? "");
        _pkgEnemies       = LoadPackaged("TwitchOverlayMod.data.enemies.json",
            e => e.GameId ?? "");
        _pkgCards         = LoadPackaged("TwitchOverlayMod.data.cards.json",
            e => $"{e.GameId}:{e.UpgradeLevel}");
        _pkgEnchantments  = LoadPackaged("TwitchOverlayMod.data.enchantments.json",
            e => e.GameId ?? "");
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

    private void ScanEnchantments()
    {
        var allProp = typeof(ModelDb).GetProperty("DebugEnchantments", BindingFlags.Public | BindingFlags.Static);
        if (allProp?.GetValue(null) is not System.Collections.IEnumerable all) return;

        foreach (var enc in all)
        {
            try
            {
                var encType = enc.GetType();
                var gameId  = encType.GetProperty("Id")?.GetValue(enc)
                                    ?.GetType().GetProperty("Entry")?.GetValue(
                                        encType.GetProperty("Id")!.GetValue(enc))?.ToString() ?? "";
                if (string.IsNullOrEmpty(gameId)) continue;

                var liveName = EnchFormattedText(enc, encType, "Name", "Title") ?? "";
                var liveDesc = EnchFormattedText(enc, encType, "Description", "DynamicDescription") ?? "";

                var pkgId = EnchantmentIdMapper.GetSequentialId(gameId);
                if (pkgId == null)
                {
                    var capturedEnc = enc;
                    var item = new BackfillItem
                    {
                        Category = "enchantments", Key = gameId, IsNew = true,
                        Name = liveName, Description = liveDesc,
                        ExtraFields = new()
                        {
                            ["game_id"]           = gameId,
                            ["is_enchantment"]    = true,
                            ["plain_description"] = liveDesc,
                            ["loc_plain_description"] = true,
                            ["image"]             = (object?)null
                        },
                        TextureGetter = () => EnchGetIcon(capturedEnc)
                    };
                    _pendingLoc.Add((item, null, null));
                    _scanned.Add(item);
                }
                else if (_pkgEnchantments.TryGetValue(gameId, out var pkg) && ContentChanged(pkg, liveName, liveDesc))
                {
                    _scanned.Add(new BackfillItem
                    {
                        Category = "enchantments", Key = gameId, IsNew = false,
                        Id = pkgId.Value,
                        Name = liveName, Description = liveDesc,
                        ExtraFields = new()
                        {
                            ["game_id"]           = gameId,
                            ["is_enchantment"]    = true,
                            ["plain_description"] = liveDesc,
                            ["loc_plain_description"] = true,
                            ["image"]             = pkg.Image
                        }
                    });
                }
            }
            catch { }
        }
    }

    private static string? EnchFormattedText(object enc, Type t, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var val    = t.GetProperty(name)?.GetValue(enc);
                var method = val?.GetType().GetMethod("GetFormattedText");
                if (method != null) return method.Invoke(val, null)?.ToString();
            }
            catch { }
        }
        return null;
    }

    private static Texture2D? EnchGetIcon(object enc)
    {
        var t = enc.GetType();
        foreach (var name in new[] { "Icon", "BigIcon", "SmallIcon", "Sprite" })
        {
            try { if (t.GetProperty(name)?.GetValue(enc) is Texture2D tex) return tex; }
            catch { }
        }
        return null;
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
        "relics"        => 288,
        "powers"        => 269,
        "potions"       => 63,
        "intents"       => 16,
        "enemies"       => 101,
        "cards"         => 1116,
        "enchantments"  => 23,
        _               => 0
    };

    private void RegisterWithMapper(BackfillItem item)
    {
        try
        {
            switch (item.Category)
            {
                case "relics":        RelicIdMapper.Register(item.Key, item.Id);        break;
                case "powers":        PowerIdMapper.Register(item.Key, item.Id);        break;
                case "potions":       PotionIdMapper.Register(item.Key, item.Id);       break;
                case "enemies":       EnemyIdMapper.Register(item.Key, item.Id);        break;
                case "enchantments":  EnchantmentIdMapper.Register(item.Key, item.Id);  break;
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

        // Brand-new items: cards reference a separate frame key; other items send
        // their captured image as art chunks (CapturedWebP set during Scan).
        if (item.IsNew)
        {
            if (item.Category == "cards")
            {
                item.ImageNote = item.CapturedWebP != null ? "split" : "no capture";
                dict["frame"]  = GetFrameKey(item);
            }
            else
            {
                item.ImageNote = item.CapturedWebP != null ? null : "no capture";
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
        TryCaptureLiveCharacterPointer();
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

        // Frame capture phase.
        Queue<(string key, BackfillItem rep)>? frameQueue     = null;
        string                                 currentFrameKey = "";
        bool                                   framePhaseA     = false;
        var                                    hiddenLabels    = new List<Label>();
        var                                    hiddenRtls      = new List<RichTextLabel>();
        Rect2I                                 titleRect       = default;
        Rect2I                                 costRect        = default;
        Rect2I                                 descRect        = default;

        void SetupNext()
        {
            foreach (var ci in hiddenItems) ci.Visible = true;
            hiddenItems.Clear();
            artNode     = null;
            artRect     = default;
            initialDone = false;

            if (queue.Count == 0)
            {
                // Transition to frame capture phase.
                if (frameQueue == null)
                {
                    frameQueue = new Queue<(string key, BackfillItem rep)>();
                    var seen = new HashSet<string>();
                    foreach (var si in _scanned.Where(si => si.IsNew && si.Category == "cards"))
                    {
                        var fk = GetFrameKey(si);
                        if (!seen.Add(fk) || _capturedFrames.ContainsKey(fk)) continue;
                        frameQueue.Enqueue((fk, si));
                    }
                    Logging.Log($"[Backfill] Frame capture queue: {frameQueue.Count} unique key(s)");
                }

                if (frameQueue.Count == 0)
                {
                    timer?.QueueFree();
                    viewport!.QueueFree();
                    Logging.Log($"[Backfill] Art+frame capture done: {captured}/{items.Count} card(s)");
                    ConsolePrint($"Art+frame capture done: {captured}/{items.Count} captured");
                    onComplete();
                    return;
                }

                // Set up card for the next frame key.
                var (fkey, rep) = frameQueue.Dequeue();
                currentFrameKey = fkey;
                framePhaseA     = false;

                var fp      = rep.Key.Split(':');
                var fgameId = fp[0];
                var flv     = fp.Length > 1 && int.TryParse(fp[1], out var fli) ? fli : 0;
                var fcm     = ModelDb.AllCards.FirstOrDefault(c => c.Id.ToString() == fgameId);
                if (fcm == null) { SetupNext(); return; }

                try
                {
                    var fmut = fcm.ToMutable();
                    for (int fi = 0; fi < flv && fmut.CurrentUpgradeLevel < fmut.MaxUpgradeLevel; fi++)
                    {
                        fmut.UpgradeInternal();
                        fmut.FinalizeUpgradeInternal();
                    }
                    card!.Model = fmut;
                    card.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                }
                catch (Exception ex)
                {
                    Logging.Log($"[Backfill] Frame setup error: {ex.Message}");
                    SetupNext();
                    return;
                }

                current      = null; // signals OnTick to call OnFrameTick
                framesToWait = 2;
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
                if (current == null && frameQueue != null) { OnFrameTick(); return; }
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

        void OnFrameTick()
        {
            if (!framePhaseA)
            {
                // Phase A: measure text/art positions then blank them so only the frame renders.
                artNode   = FindArtNode(card!);
                artRect   = artNode != null ? GetGlobalRectI(artNode) : default;
                titleRect = MeasureFirstByNames<Label>(card!, "Title", "CardTitle", "TitleLabel", "Name", "CardName");
                costRect  = MeasureFirstByNames<Label>(card!, "EnergyLabel", "Cost", "CardCost", "EnergyCost", "CostLabel");
                descRect  = MeasureFirstByNames<RichTextLabel>(card!, "Description", "CardDesc", "DescriptionLabel", "Desc");

                if (artNode != null) artNode.Texture = null;

                hiddenLabels.Clear();
                hiddenRtls.Clear();
                foreach (var lbl in FindDescendants<Label>(card!))
                    if (lbl.Visible) { lbl.Visible = false; hiddenLabels.Add(lbl); }
                foreach (var rtl in FindDescendants<RichTextLabel>(card!))
                    if (rtl.Visible) { rtl.Visible = false; hiddenRtls.Add(rtl); }

                framePhaseA  = true;
                framesToWait = 2;
                return;
            }

            // Phase B: capture the clean frame image.
            try
            {
                var full = viewport!.GetTexture().GetImage();
                var iw   = full.GetWidth();
                var ih   = full.GetHeight();
                const int MaxFrameW = 200;
                if (iw > MaxFrameW && iw > 0)
                    full.Resize(MaxFrameW, (int)((float)MaxFrameW / iw * ih), Image.Interpolation.Bilinear);
                _capturedFrames[currentFrameKey] = new FrameCapture
                {
                    WebP    = full.SaveWebpToBuffer(true),
                    CardW   = iw, CardH   = ih,
                    ArtX    = artRect.Position.X, ArtY = artRect.Position.Y,
                    ArtW    = artRect.Size.X,     ArtH = artRect.Size.Y,
                    TitleCx = CenterX(titleRect), TitleCy = CenterY(titleRect),
                    TitleW  = titleRect.Size.X,
                    CostCx  = CenterX(costRect),  CostCy  = CenterY(costRect),
                    DescX   = descRect.Position.X, DescY  = descRect.Position.Y,
                    DescW   = descRect.Size.X,     DescH  = descRect.Size.Y
                };
                ConsolePrint($"Frame captured: {currentFrameKey}");
            }
            catch (Exception ex)
            {
                Logging.Log($"[Backfill] Frame capture error {currentFrameKey}: {ex.Message}");
            }

            foreach (var lbl in hiddenLabels) lbl.Visible = true;
            hiddenLabels.Clear();
            foreach (var rtl in hiddenRtls) rtl.Visible = true;
            hiddenRtls.Clear();

            framePhaseA = false;
            SetupNext();
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

    private void CaptureNonCardImages()
    {
        foreach (var item in _scanned)
        {
            if (!item.IsNew || item.Category == "cards" || item.TextureGetter == null || item.CapturedWebP != null)
                continue;
            try
            {
                var tex = item.TextureGetter();
                if (tex == null) continue;
                var img = tex.GetImage();
                if (img == null) continue;
                const int MaxDim = 120;
                if (img.GetWidth() > MaxDim || img.GetHeight() > MaxDim)
                {
                    var s = Math.Min((float)MaxDim / img.GetWidth(), (float)MaxDim / img.GetHeight());
                    img.Resize((int)(img.GetWidth() * s), (int)(img.GetHeight() * s), Image.Interpolation.Bilinear);
                }
                item.CapturedWebP = img.SaveWebpToBuffer(true);
            }
            catch { }
        }
    }

    // Called every tick-2 when a run is active. The player's Character instance is
    // available directly here — no singleton detection or pool-title guessing needed.
    // charId matches player.CharacterId in the game-state payload so the viewer lookup
    // is guaranteed to resolve.
    private void TryCaptureLiveCharacterPointer()
    {
        try
        {
            var runState = RunManager.Instance?.DebugOnlyGetState();
            var player   = runState?.Players.FirstOrDefault();
            if (player == null) return;

            var character = player.Character;
            var charId    = character.Id.Entry.ToLowerInvariant();

            if (_capturedPointers.ContainsKey(charId)) return; // already have it

            var tex = GetMarkerFromCharInstance(character.GetType(), character);
            if (tex == null)
            {
                Logging.Log($"[Backfill] Live pointer: no marker found for {charId}");
                return;
            }

            var img = tex.GetImage();
            if (img == null) return;
            const int MaxDim = 64;
            if (img.GetWidth() > MaxDim || img.GetHeight() > MaxDim)
            {
                var s = Math.Min((float)MaxDim / img.GetWidth(), (float)MaxDim / img.GetHeight());
                img.Resize((int)(img.GetWidth() * s), (int)(img.GetHeight() * s), Image.Interpolation.Bilinear);
            }
            _capturedPointers[charId] = img.SaveWebpToBuffer(true);
            Logging.Log($"[Backfill] Live pointer captured for {charId} ({img.GetWidth()}x{img.GetHeight()})");
        }
        catch (Exception ex)
        {
            Logging.Log($"[Backfill] Live pointer capture error: {ex.Message}");
        }
    }

    private void CapturePoolPointers()
    {
        var seen = new HashSet<string>();
        foreach (var item in _scanned.Where(i => i.IsNew && i.Category == "cards" && i.Key.EndsWith(":0")))
        {
            var poolName = (item.ExtraFields.TryGetValue("pool", out var p) ? p?.ToString() : null) ?? "";
            var charId   = poolName.ToLowerInvariant();
            if (!seen.Add(charId)) continue;

            var gameId = item.Key.Split(':')[0];
            var card   = ModelDb.AllCards.FirstOrDefault(c => c.Id.ToString() == gameId);
            var tex    = GetCharacterMapMarker(charId, card?.Pool);
            if (tex == null) continue;
            try
            {
                var img = tex.GetImage();
                if (img == null) continue;
                const int MaxDim = 64;
                if (img.GetWidth() > MaxDim || img.GetHeight() > MaxDim)
                {
                    var s = Math.Min((float)MaxDim / img.GetWidth(), (float)MaxDim / img.GetHeight());
                    img.Resize((int)(img.GetWidth() * s), (int)(img.GetHeight() * s), Image.Interpolation.Bilinear);
                }
                _capturedPointers[charId] = img.SaveWebpToBuffer(true);
                Logging.Log($"[Backfill] Captured pool pointer for {charId}");
            }
            catch { }
        }
    }

    // Multi-strategy lookup for a character's map marker texture.
    // Never calls unknown constructors — safe to run at scan time.
    private static Texture2D? GetCharacterMapMarker(string charId, object? pool)
    {
        // 1. Base-game characters (fast path, hard-coded AllCharacters array).
        try
        {
            var m = ModelDb.AllCharacters.FirstOrDefault(c => c.Id.Entry.ToLowerInvariant() == charId);
            if (m != null) return m.MapMarker;
        }
        catch { }

        if (pool == null) return null;
        var poolType = pool.GetType();

        // 2. Texture2D-named properties directly on the pool object.
        foreach (var name in new[] { "MapMarker", "MapPointer", "MapIcon", "SmallPortrait", "Portrait",
                                     "PortraitTexture", "CharacterIcon", "Icon", "Thumbnail" })
        {
            try
            {
                var prop = poolType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop?.GetValue(pool) is Texture2D tex) return tex;
            }
            catch { }
        }

        // 3. String path properties on the pool → ResourceLoader (e.g. CustomMapMarkerPath on a pool).
        foreach (var name in new[] { "CustomMapMarkerPath", "MapMarkerPath", "MarkerPath" })
        {
            try
            {
                var prop = poolType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                var path = prop?.GetValue(pool) as string;
                if (!string.IsNullOrEmpty(path))
                {
                    var tex = ResourceLoader.Load<Texture2D>(path);
                    if (tex != null) return tex;
                }
            }
            catch { }
        }

        // 4. CharacterModel-typed property on the pool (back-reference from pool → character).
        var charModelType = typeof(CharacterModel);
        foreach (var prop in poolType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try
            {
                if (!charModelType.IsAssignableFrom(prop.PropertyType)) continue;
                if (prop.GetValue(pool) is not CharacterModel inst) continue;
                var tex = GetMarkerFromCharInstance(inst.GetType(), inst);
                if (tex != null) return tex;
            }
            catch { }
        }

        // 5. Scan the pool's own assembly for CharacterModel subclasses; use static Instance
        //    singleton if available (no allocation). Handles BaseLib-style mods (e.g. Watcher)
        //    where the character class is separate from the pool class.
        Type[] asmTypes;
        try { asmTypes = poolType.Assembly.GetTypes(); }
        catch { asmTypes = []; }

        foreach (var type in asmTypes)
        {
            if (!type.IsClass || type.IsAbstract || !charModelType.IsAssignableFrom(type) || type == charModelType)
                continue;
            try
            {
                var instanceProp = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp?.GetValue(null) is not CharacterModel inst) continue;
                if (inst.Id.Entry.ToLowerInvariant() != charId) continue;
                var tex = GetMarkerFromCharInstance(type, inst);
                if (tex != null) return tex;
            }
            catch { }

            // 6. ModelDb.Character<T>() — standard registration path for mods that use it.
            try
            {
                var method = typeof(ModelDb).GetMethod("Character", BindingFlags.Public | BindingFlags.Static);
                if (method == null) continue;
                var generic = method.MakeGenericMethod(type);
                if (generic.Invoke(null, null) is not CharacterModel inst2) continue;
                if (inst2.Id.Entry.ToLowerInvariant() != charId) continue;
                var tex = GetMarkerFromCharInstance(type, inst2);
                if (tex != null) return tex;
            }
            catch { }
        }

        return null;
    }

    private static Texture2D? GetMarkerFromCharInstance(Type type, CharacterModel inst)
    {
        // Standard base-class property.
        try
        {
            var tex = inst.MapMarker;
            if (tex != null) { Logging.Log($"[Backfill] Pointer via MapMarker ({type.Name})"); return tex; }
        }
        catch (Exception ex) { Logging.Log($"[Backfill] MapMarker failed ({type.Name}): {ex.Message}"); }

        // CustomMapMarkerPath: a string-path convention used by BaseLib mods.
        try
        {
            var pathProp = type.GetProperty("CustomMapMarkerPath",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            var path = pathProp?.GetValue(inst) as string;
            Logging.Log($"[Backfill] CustomMapMarkerPath ({type.Name}) = {path ?? "(null)"}");
            if (!string.IsNullOrEmpty(path))
            {
                var tex = ResourceLoader.Load<Texture2D>(path);
                if (tex != null) { Logging.Log($"[Backfill] Pointer via CustomMapMarkerPath ({type.Name})"); return tex; }
                Logging.Log($"[Backfill] ResourceLoader returned null for path: {path}");
            }
        }
        catch (Exception ex) { Logging.Log($"[Backfill] CustomMapMarkerPath failed ({type.Name}): {ex.Message}"); }

        return null;
    }

    private static string GetFrameKey(BackfillItem item)
    {
        string Pool()   => (item.ExtraFields.TryGetValue("pool",   out var v) ? v?.ToString() : null) ?? "unknown";
        string Type()   => (item.ExtraFields.TryGetValue("type",   out var v) ? v?.ToString() : null) ?? "unknown";
        string Rarity() => (item.ExtraFields.TryGetValue("rarity", out var v) ? v?.ToString() : null) ?? "unknown";
        var lv       = item.Key.Contains(':') ? item.Key.Split(':')[1] : "0";
        var upgraded = lv == "0" ? "base" : "upgraded";
        var ec       = item.ExtraFields.TryGetValue("energy_cost", out var ecv) && ecv is int ei ? ei : 0;
        var cx       = item.ExtraFields.TryGetValue("costs_x",     out var cxv) && cxv is bool bv  && bv;
        var norb     = (ec < 0 && !cx) ? "_norb" : "";
        return $"{Pool().ToLowerInvariant()}_{Type().ToLowerInvariant()}_{Rarity().ToLowerInvariant()}_{upgraded}{norb}";
    }

    private void EnqueueFrameChunk(string key, FrameCapture fc)
    {
        if (fc.WebP == null) return;
        var b64        = Convert.ToBase64String(fc.WebP);
        const int Sz   = 4700;
        var total      = (b64.Length + Sz - 1) / Sz;
        var escapedKey = JsonSerializer.Serialize(key);
        for (int part = 1; part <= total; part++)
        {
            var start  = (part - 1) * Sz;
            var len    = Math.Min(Sz, b64.Length - start);
            var layout = part == 1
                ? $",\"card_w\":{fc.CardW},\"card_h\":{fc.CardH}" +
                  $",\"art_x\":{fc.ArtX},\"art_y\":{fc.ArtY},\"art_w\":{fc.ArtW},\"art_h\":{fc.ArtH}" +
                  $",\"title_cx\":{fc.TitleCx},\"title_cy\":{fc.TitleCy},\"title_w\":{fc.TitleW}" +
                  $",\"cost_cx\":{fc.CostCx},\"cost_cy\":{fc.CostCy}" +
                  $",\"desc_x\":{fc.DescX},\"desc_y\":{fc.DescY},\"desc_w\":{fc.DescW},\"desc_h\":{fc.DescH}"
                : "";
            _largeChunks.Enqueue((
                $"{{\"t\":\"frame\",\"key\":{escapedKey},\"part\":{part},\"of\":{total}{layout},\"data\":\"{b64.Substring(start, len)}\"}}",
                $"frame/{key} p{part}/{total}"));
        }
        Logging.Log($"[Backfill] Enqueued frame chunks for key={key} ({fc.WebP.Length}b)");
    }

    private void EnqueuePointerChunk(string charId, byte[] webp)
    {
        var b64       = Convert.ToBase64String(webp);
        const int Sz  = 4700;
        var total     = (b64.Length + Sz - 1) / Sz;
        var escapedId = JsonSerializer.Serialize(charId);
        for (int part = 1; part <= total; part++)
        {
            var start = (part - 1) * Sz;
            var len   = Math.Min(Sz, b64.Length - start);
            _largeChunks.Enqueue((
                $"{{\"t\":\"pointer\",\"char_id\":{escapedId},\"part\":{part},\"of\":{total},\"data\":\"{b64.Substring(start, len)}\"}}",
                $"pointer/{charId} p{part}/{total}"));
        }
    }

    private static Rect2I MeasureFirstByNames<T>(Node root, params string[] names) where T : Control
    {
        foreach (var name in names)
            if (FindDescendantByName<T>(root, name) is { } found) return GetGlobalRectI(found);
        return default;
    }

    private static int CenterX(Rect2I r) => r.Size.X > 0 ? r.Position.X + r.Size.X / 2 : 0;
    private static int CenterY(Rect2I r) => r.Size.Y > 0 ? r.Position.Y + r.Size.Y / 2 : 0;

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

internal class FrameCapture
{
    public byte[]? WebP;
    public int CardW, CardH;
    public int ArtX, ArtY, ArtW, ArtH;
    public int TitleCx, TitleCy, TitleW;
    public int CostCx, CostCy;
    public int DescX, DescY, DescW, DescH;
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
