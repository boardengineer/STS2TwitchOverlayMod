using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using TwitchOverlayMod.Models;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.State;

internal static class GameStateCollector
{
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

        return payload;
    }

    private static PlayerInfo? CollectPlayerInfo()
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null) return null;

        var player = runState.Players.FirstOrDefault();
        if (player == null) return null;

        var info = new PlayerInfo
        {
            CurrentHp = player.Creature.CurrentHp,
            MaxHp = player.Creature.MaxHp,
            Gold = player.Gold,
            CurrentAct = runState.CurrentActIndex,
            ActFloor = runState.ActFloor
        };

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
            }

            info.Relics.Add(relicInfo);
        }

        foreach (var potion in player.PotionSlots)
        {
            if (potion == null)
            {
                info.Potions.Add(null);
            }
            else
            {
                info.Potions.Add(new PotionInfo
                {
                    Id = potion.Id.ToString(),
                    Name = potion.Title.GetRawText()
                });
            }
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
            DrawPileCount = pcs.DrawPile.Cards.Count,
            DiscardPileCount = pcs.DiscardPile.Cards.Count
        };

        foreach (var card in pcs.Hand.Cards)
        {
            var seqId = CardIdMapper.GetSequentialId(card.Id.ToString(), card.CurrentUpgradeLevel);
            if (seqId.HasValue)
                info.Hand.Add(seqId.Value);
        }

        foreach (var enemy in combatState.Enemies)
        {
            if (enemy.IsDead) continue;

            var enemyInfo = new EnemyInfo
            {
                Id = EnemyIdMapper.GetSequentialId(enemy.Name) ?? -1,
                CurrentHp = enemy.CurrentHp,
                MaxHp = enemy.MaxHp,
                Block = enemy.Block,
                IntentId = GetIntentId(enemy)
            };

            foreach (var power in enemy.Powers)
            {
                enemyInfo.Powers.Add(new PowerInfo
                {
                    Id = power.Id.ToString(),
                    Name = power.Title.GetRawText(),
                    Amount = power.Amount
                });
            }

            info.Enemies.Add(enemyInfo);
        }

        return info;
    }

    private static string GetIntentId(Creature enemy)
    {
        var move = enemy.Monster?.NextMove;
        if (move == null) return "unknown";

        var intents = move.Intents;
        if (intents == null || intents.Count == 0) return "unknown";

        return string.Join(",", intents.Select(i => i.IntentType.ToString()));
    }

    private static UiInfo? CollectUiInfo()
    {
        var deckButton = NRun.Instance?.GlobalUi.TopBar.Deck;
        if (deckButton == null) return null;

        var windowSize = Godot.DisplayServer.WindowGetSize();
        var screenTransform = deckButton.GetViewport().GetScreenTransform();
        var deckRect = screenTransform * deckButton.GetGlobalRect();

        return new UiInfo
        {
            WindowWidth = windowSize.X,
            WindowHeight = windowSize.Y,
            DeckButtonX = deckRect.Position.X,
            DeckButtonY = deckRect.Position.Y,
            DeckButtonWidth = deckRect.Size.X,
            DeckButtonHeight = deckRect.Size.Y
        };
    }
}
