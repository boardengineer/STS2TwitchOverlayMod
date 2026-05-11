using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using TwitchOverlayMod.Models;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.State;

internal static class GameStateCollector
{
    private static readonly FieldInfo? PotionHoldersField =
        typeof(NPotionContainer).GetField("_holders", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? EventRoomEventField =
        typeof(NEventRoom).GetField("_event", BindingFlags.Instance | BindingFlags.NonPublic);

    private static Dictionary<string, CardKeyword>? _kwIdMap;
    private static Dictionary<string, CardKeyword> KeywordIdMap
    {
        get
        {
            if (_kwIdMap != null) return _kwIdMap;
            _kwIdMap = new Dictionary<string, CardKeyword>();
            foreach (var kw in Enum.GetValues<CardKeyword>())
            {
                if (kw == CardKeyword.None) continue;
                try { var id = HoverTipFactory.FromKeyword(kw).Id; if (!string.IsNullOrEmpty(id)) _kwIdMap[id] = kw; }
                catch { }
            }
            return _kwIdMap;
        }
    }

    // Maps HoverTip.Id (LocString key) → enchantment game_id (e.g. "Spiral")
    private static Dictionary<string, string>? _enchTipIdMap;
    private static Dictionary<string, string> EnchantmentTipIdMap
    {
        get
        {
            if (_enchTipIdMap != null) return _enchTipIdMap;
            _enchTipIdMap = new Dictionary<string, string>();
            try
            {
                var allProp = typeof(ModelDb).GetProperty("DebugEnchantments", BindingFlags.Public | BindingFlags.Static);
                if (allProp?.GetValue(null) is not System.Collections.IEnumerable all) return _enchTipIdMap;
                foreach (var enc in all)
                {
                    try
                    {
                        var encType = enc.GetType();
                        var idVal   = encType.GetProperty("Id")?.GetValue(enc);
                        var gameId  = idVal?.GetType().GetProperty("Entry")?.GetValue(idVal)?.ToString();
                        if (string.IsNullOrEmpty(gameId)) continue;

                        var htProp  = encType.GetProperty("HoverTip", BindingFlags.Public | BindingFlags.Instance);
                        var ht      = htProp?.GetValue(enc);
                        var tipId   = ht?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(ht)?.ToString();
                        if (!string.IsNullOrEmpty(tipId)) _enchTipIdMap.TryAdd(tipId, gameId);
                    }
                    catch { }
                }
            }
            catch { }
            return _enchTipIdMap;
        }
    }

    private static Models.HoverTipRef? TryBuildHoverTipRef(IHoverTip tip)
    {
        if (tip.CanonicalModel is PowerModel pm)
            return new Models.HoverTipRef { Type = "power",   Id = pm.Id.Entry };
        if (tip.CanonicalModel is RelicModel rm)
            return new Models.HoverTipRef { Type = "relic",   Id = rm.Id.ToString() };
        if (tip.CanonicalModel is PotionModel pot)
            return new Models.HoverTipRef { Type = "potion",  Id = pot.Id.Entry };
        // CardModel: skip — the card's own keyword tips are already in the same HoverTips list
        if (tip.CanonicalModel is CardModel) return null;
        if (!string.IsNullOrEmpty(tip.Id) && KeywordIdMap.TryGetValue(tip.Id, out var kw))
            return new Models.HoverTipRef { Type = "keyword", Id = kw.ToString() };
        if (!string.IsNullOrEmpty(tip.Id) && EnchantmentTipIdMap.TryGetValue(tip.Id, out var enchGameId))
            return new Models.HoverTipRef { Type = "enchantment", Id = enchGameId };
        if (tip is HoverTip ht && (!string.IsNullOrEmpty(ht.Title) || !string.IsNullOrEmpty(ht.Description)))
            return new Models.HoverTipRef { Type = "inline", Title = ht.Title, Description = ht.Description, IsDebuff = tip.IsDebuff };
        return null;
    }

    internal static GameStatePayload Collect()
    {
        var payload = new GameStatePayload
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        try
        {
            payload.Player = CollectPlayerInfo();
        }
        catch (Exception ex)
        {
            Logging.Log($"Error collecting player info: {ex.Message}");
        }

        try
        {
            payload.Combat = CollectCombatInfo();
        }
        catch (Exception ex)
        {
            Logging.Log($"Error collecting combat info: {ex.Message}");
        }

        try
        {
            payload.Ui = CollectUiInfo();
        }
        catch (Exception ex)
        {
            Logging.Log($"Error collecting UI info: {ex.Message}");
        }

        try
        {
            payload.Map = CollectMapInfo();
        }
        catch (Exception ex)
        {
            Logging.Log($"Error collecting map info: {ex.Message}");
        }

        try
        {
            payload.Event = CollectEventInfo();
        }
        catch (Exception ex)
        {
            Logging.Log($"Error collecting event info: {ex.Message}");
        }

        return payload;
    }

    private static MapInfo? CollectMapInfo()
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null) return null;

        if (!MapSnapshotCache.TryGetSnapshot(runState.CurrentActIndex, out var cached)) return null;

        var states = MapSnapshotCache.ReadLiveStates();
        var current = runState.CurrentMapCoord;
        var map = new MapInfo
        {
            CurrentCol = current?.col,
            CurrentRow = current?.row
        };
        foreach (var p in cached)
        {
            map.Cols.Add(p.Col);
            map.Rows.Add(p.Row);
            map.Xs.Add(p.X);
            map.Ys.Add(p.Y);
            map.Types.Add(p.Type);
            map.States.Add(states != null && states.TryGetValue((p.Col, p.Row), out var s) ? s : 0);
            map.Children.Add(p.Children);
        }
        return map;
    }

    private static PlayerInfo? CollectPlayerInfo()
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null) return null;

        var player = runState.Players.FirstOrDefault();
        if (player == null) return null;

        var info = new PlayerInfo
        {
            CharacterId = player.Character.Id.Entry.ToLowerInvariant(),
            CurrentHp = player.Creature.CurrentHp,
            MaxHp = player.Creature.MaxHp,
            Gold = player.Gold,
            CurrentAct = runState.CurrentActIndex,
            ActFloor = runState.ActFloor,
            AscensionLevel = runState.AscensionLevel
        };

        var act = runState.Act;
        if (act != null)
        {
            try { info.ActId = act.Id.Entry.ToLowerInvariant(); } catch { }
            try { info.BossId = act.BossEncounter?.Id.Entry; } catch { }
            try { info.AncientId = act.Ancient?.Id.Entry; } catch { }
        }

        foreach (var card in player.Deck.Cards)
        {
            var seqId = CardIdMapper.GetSequentialId(card.Id.ToString(), card.CurrentUpgradeLevel);
            if (seqId.HasValue)
                info.Deck.Add(seqId.Value);
        }

        var relicNodes = NRun.Instance?.GlobalUi.RelicInventory.RelicNodes;
        var screenTransform = relicNodes?.Count > 0
            ? relicNodes[0].GetViewport().GetScreenTransform()
            : (Godot.Transform2D?)null;

        for (var i = 0; i < player.Relics.Count; i++)
        {
            var seqId = RelicIdMapper.GetSequentialId(player.Relics[i].Id.ToString());
            if (!seqId.HasValue) continue;

            var relicInfo = new RelicInfo { Id = seqId.Value };

            if (screenTransform.HasValue && relicNodes != null && i < relicNodes.Count)
            {
                var rect = screenTransform.Value * relicNodes[i].GetGlobalRect();
                relicInfo.X = rect.Position.X;
                relicInfo.Y = rect.Position.Y;
                relicInfo.Width = rect.Size.X;
                relicInfo.Height = rect.Size.Y;
            }

            info.Relics.Add(relicInfo);
        }

        var potionContainer = NRun.Instance?.GlobalUi.TopBar.PotionContainer;
        var holders = PotionHoldersField?.GetValue(potionContainer) as List<NPotionHolder>;
        for (var i = 0; i < player.PotionSlots.Count; i++)
        {
            var potion = player.PotionSlots[i];
            var potionInfo = new PotionInfo
            {
                Id = potion == null
                    ? -1
                    : (PotionIdMapper.GetSequentialId(potion.Id.Entry) ?? -1)
            };
            if (holders != null && i < holders.Count && holders[i] is Godot.Control holder)
            {
                var rect = holder.GetViewport().GetScreenTransform() * holder.GetGlobalRect();
                potionInfo.X = rect.Position.X;
                potionInfo.Y = rect.Position.Y;
                potionInfo.Width = rect.Size.X;
                potionInfo.Height = rect.Size.Y;
            }
            info.Potions.Add(potionInfo);
        }

        return info;
    }

    private static CombatInfo? CollectCombatInfo()
    {
        var combatState = CombatManager.Instance?.DebugOnlyGetState();
        if (combatState == null) return null;

        var player = combatState.Players.FirstOrDefault();
        if (player?.PlayerCombatState == null) return null;

        var pcs = player.PlayerCombatState;
        var info = new CombatInfo
        {
            Energy = pcs.Energy,
            MaxEnergy = pcs.MaxEnergy,
            Block = player.Creature.Block,
            DrawPileCount = pcs.DrawPile.Cards.Count,
            DiscardPileCount = pcs.DiscardPile.Cards.Count
        };

        var powerNodes = new Dictionary<PowerModel, NPower>();
        var combatRoom = NCombatRoom.Instance;
        Godot.Transform2D screenTransform = default;
        if (combatRoom != null)
        {
            CollectNPowers(combatRoom, powerNodes);
            screenTransform = combatRoom.GetViewport().GetScreenTransform();
        }

        foreach (var power in player.Creature.Powers)
            info.Powers.Add(MakePowerInfo(power, powerNodes, screenTransform));

        foreach (var card in pcs.Hand.Cards)
        {
            var seqId = CardIdMapper.GetSequentialId(card.Id.ToString(), card.CurrentUpgradeLevel);
            if (seqId.HasValue)
                info.Hand.Add(seqId.Value);
        }

        foreach (var card in pcs.DrawPile.Cards)
        {
            var seqId = CardIdMapper.GetSequentialId(card.Id.ToString(), card.CurrentUpgradeLevel);
            if (seqId.HasValue)
                info.DrawPile.Add(seqId.Value);
        }

        foreach (var card in pcs.DiscardPile.Cards)
        {
            var seqId = CardIdMapper.GetSequentialId(card.Id.ToString(), card.CurrentUpgradeLevel);
            if (seqId.HasValue)
                info.DiscardPile.Add(seqId.Value);
        }

        foreach (var card in pcs.ExhaustPile.Cards)
        {
            var seqId = CardIdMapper.GetSequentialId(card.Id.ToString(), card.CurrentUpgradeLevel);
            if (seqId.HasValue)
                info.ExhaustPile.Add(seqId.Value);
        }

        var playerHitbox = combatRoom?.GetCreatureNode(player.Creature)?.Hitbox;
        if (playerHitbox != null)
        {
            var playerRect = screenTransform * playerHitbox.GetGlobalRect();
            info.PlayerHitboxX = playerRect.Position.X;
            info.PlayerHitboxY = playerRect.Position.Y;
            info.PlayerHitboxWidth = playerRect.Size.X;
            info.PlayerHitboxHeight = playerRect.Size.Y;
        }

        var targets = combatState.Players.Select(p => p.Creature).ToList();
        foreach (var enemy in combatState.Enemies)
        {
            if (enemy.IsDead) continue;

            var enemyInfo = new EnemyInfo
            {
                Id = EnemyIdMapper.GetSequentialId(enemy.Name) ?? -1,
                CurrentHp = enemy.CurrentHp,
                MaxHp = enemy.MaxHp,
                Block = enemy.Block
            };

            var intent = enemy.Monster?.NextMove?.Intents?.FirstOrDefault();
            if (intent != null)
            {
                var animation = intent.GetAnimation(targets, enemy);
                // "status" is StatusIntent's animation key; it maps to the same tooltip as "status_card"
                if (animation == "status") animation = "status_card";
                enemyInfo.Intent = IntentIdMapper.GetSequentialId(animation);
                if (enemyInfo.Intent == null)
                    Logging.Log($"[IntentIdMapper] Unknown animation type '{animation}' for enemy '{enemy.Name}' (type: {intent.GetType().Name})");
                try { enemyInfo.IntentLabel = intent.GetIntentLabel(targets, enemy).GetFormattedText(); } catch { }
                if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk)
                {
                    try { enemyInfo.IntentDamage = atk.GetSingleDamage(targets, enemy); } catch { }
                    try { enemyInfo.IntentRepeat = atk.Repeats; } catch { }
                }
            }

            var creatureNode = combatRoom?.GetCreatureNode(enemy);
            var intentNode = creatureNode?.IntentContainer?.GetChildOrNull<NIntent>(0);
            if (intentNode != null)
            {
                var r = screenTransform * intentNode.GetGlobalRect();
                enemyInfo.IntentX = r.Position.X;
                enemyInfo.IntentY = r.Position.Y;
                enemyInfo.IntentW = r.Size.X;
                enemyInfo.IntentH = r.Size.Y;
            }

            foreach (var power in enemy.Powers)
                enemyInfo.Powers.Add(MakePowerInfo(power, powerNodes, screenTransform));

            var enemyHitbox = combatRoom?.GetCreatureNode(enemy)?.Hitbox;
            if (enemyHitbox != null)
            {
                var enemyRect = screenTransform * enemyHitbox.GetGlobalRect();
                enemyInfo.X = enemyRect.Position.X;
                enemyInfo.Y = enemyRect.Position.Y;
                enemyInfo.Width = enemyRect.Size.X;
                enemyInfo.Height = enemyRect.Size.Y;
            }

            info.Enemies.Add(enemyInfo);
        }

        var combatUi = combatRoom?.Ui;
        if (combatUi != null)
        {
            var drawRect = screenTransform * combatUi.DrawPile.GetGlobalRect();
            info.DrawPileButtonX = drawRect.Position.X;
            info.DrawPileButtonY = drawRect.Position.Y;
            info.DrawPileButtonWidth = drawRect.Size.X;
            info.DrawPileButtonHeight = drawRect.Size.Y;

            var discardRect = screenTransform * combatUi.DiscardPile.GetGlobalRect();
            info.DiscardPileButtonX = discardRect.Position.X;
            info.DiscardPileButtonY = discardRect.Position.Y;
            info.DiscardPileButtonWidth = discardRect.Size.X;
            info.DiscardPileButtonHeight = discardRect.Size.Y;

            var exhaustRect = screenTransform * combatUi.ExhaustPile.GetGlobalRect();
            info.ExhaustPileButtonX = exhaustRect.Position.X;
            info.ExhaustPileButtonY = exhaustRect.Position.Y;
            info.ExhaustPileButtonWidth = exhaustRect.Size.X;
            info.ExhaustPileButtonHeight = exhaustRect.Size.Y;
        }

        return info;
    }

    private static void CollectNPowers(Godot.Node node, Dictionary<PowerModel, NPower> dict)
    {
        if (node is NPower np)
        {
            try { dict[np.Model] = np; }
            catch { }
        }
        foreach (var child in node.GetChildren())
            CollectNPowers(child, dict);
    }

    private static PowerInfo MakePowerInfo(PowerModel power, Dictionary<PowerModel, NPower> nodes, Godot.Transform2D screenTransform)
    {
        var info = new PowerInfo
        {
            Id = PowerIdMapper.GetSequentialId(power.Id.Entry) ?? -1,
            Amount = power.Amount
        };
        if (nodes.TryGetValue(power, out var nPower))
        {
            var rect = screenTransform * nPower.GetGlobalRect();
            info.X = rect.Position.X;
            info.Y = rect.Position.Y;
            info.Width = rect.Size.X;
            info.Height = rect.Size.Y;
        }
        foreach (var v in power.DynamicVars)
            info.Vars[v.Key] = (float)v.Value.BaseValue;
        return info;
    }

    private static UiInfo? CollectUiInfo()
    {
        var topBar = NRun.Instance?.GlobalUi.TopBar;
        var deckButton = topBar?.Deck;
        if (deckButton == null) return null;

        var windowSize = Godot.DisplayServer.WindowGetSize();
        var screenTransform = deckButton.GetViewport().GetScreenTransform();
        var deckRect = screenTransform * deckButton.GetGlobalRect();

        var info = new UiInfo
        {
            WindowWidth = windowSize.X,
            WindowHeight = windowSize.Y,
            Language = SaveManager.Instance?.SettingsSave?.Language,
            DeckButtonX = deckRect.Position.X,
            DeckButtonY = deckRect.Position.Y,
            DeckButtonWidth = deckRect.Size.X,
            DeckButtonHeight = deckRect.Size.Y
        };

        var mapButton = topBar?.Map;
        if (mapButton != null)
        {
            var mapRect = screenTransform * mapButton.GetGlobalRect();
            info.MapButtonX = mapRect.Position.X;
            info.MapButtonY = mapRect.Position.Y;
            info.MapButtonWidth = mapRect.Size.X;
            info.MapButtonHeight = mapRect.Size.Y;
        }

        var portrait = topBar?.Portrait;
        if (portrait != null)
        {
            var portraitRect = screenTransform * portrait.GetGlobalRect();
            info.AscensionWidgetX = portraitRect.Position.X;
            info.AscensionWidgetY = portraitRect.Position.Y;
            info.AscensionWidgetWidth = portraitRect.Size.X;
            info.AscensionWidgetHeight = portraitRect.Size.Y;
        }

        var settingsButton = topBar?.Pause;
        if (settingsButton != null)
        {
            var settingsRect = screenTransform * settingsButton.GetGlobalRect();
            info.SettingsButtonX = settingsRect.Position.X;
            info.SettingsButtonY = settingsRect.Position.Y;
            info.SettingsButtonWidth = settingsRect.Size.X;
            info.SettingsButtonHeight = settingsRect.Size.Y;
        }

        return info;
    }

    private static Models.EventInfo? CollectEventInfo()
    {
        var eventRoom = NEventRoom.Instance;
        if (eventRoom == null) return null;

        var eventModel = EventRoomEventField?.GetValue(eventRoom) as EventModel;
        if (eventModel?.Owner == null) return null;

        var info = new Models.EventInfo();
        try { info.Title = eventModel.Title.GetFormattedText(); } catch { }

        try
        {
            var desc = eventModel.Description;
            if (desc != null) info.Description = desc.GetFormattedText();
        }
        catch { }

        try
        {
            var layout = eventRoom.Layout;
            var buttons = layout?.OptionButtons?.ToList();
            var screenTransform = eventRoom.GetViewport().GetScreenTransform();
            var idx = 0;
            foreach (var option in eventModel.CurrentOptions)
            {
                var opt = new EventOptionInfo
                {
                    IsLocked  = option.IsLocked,
                    IsProceed = option.IsProceed,
                };
                try { opt.Title       = option.Title?.GetFormattedText(); }       catch { }
                try { opt.Description = option.Description?.GetFormattedText(); } catch { }
                try
                {
                    foreach (var tip in option.HoverTips)
                    {
                        var entry = TryBuildHoverTipRef(tip);
                        if (entry != null) opt.HoverTips.Add(entry);
                    }
                }
                catch { }
                if (buttons != null && idx < buttons.Count)
                {
                    try
                    {
                        var rect = screenTransform * buttons[idx].GetGlobalRect();
                        opt.X      = rect.Position.X;
                        opt.Y      = rect.Position.Y;
                        opt.Width  = rect.Size.X;
                        opt.Height = rect.Size.Y;
                    }
                    catch { }
                }
                info.Options.Add(opt);
                idx++;
            }
        }
        catch { }

        return info;
    }
}
