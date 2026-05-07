using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using TwitchOverlayMod.Backfill;
using TwitchOverlayMod.Config;
using TwitchOverlayMod.Scheduling;
using TwitchOverlayMod.State;
using TwitchOverlayMod.Twitch;
using TwitchOverlayMod.UI;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod;

[ModInitializer("Initialize")]
public class Plugin
{
    internal static ModConfig Config { get; private set; } = new();
    internal static BackfillManager? Backfill { get; private set; }

    internal static void SetBackfill(BackfillManager backfill) => Backfill = backfill;

    public static void Initialize()
    {
        Config = ModConfig.Load();
        Logging.Log("Config loaded successfully.");

        CardIdMapper.Load();
        Logging.Log("Card ID map loaded.");

        EnemyIdMapper.Load();
        Logging.Log("Enemy ID map loaded.");

        RelicIdMapper.Load();
        Logging.Log("Relic ID map loaded.");

        IntentIdMapper.Load();
        Logging.Log("Intent ID map loaded.");

        PowerIdMapper.Load();
        Logging.Log("Power ID map loaded.");

        PotionIdMapper.Load();
        Logging.Log("Potion ID map loaded.");

        CredentialManager.LoadSaved();
        Logging.Log("Credentials loaded.");

        var harmony = new Harmony("com.author.twitchoverlaymod");
        harmony.PatchAll(typeof(Plugin).Assembly);
    }
}

[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public class MainMenuPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMainMenu __instance)
    {
        if (NGame.Instance == null) return;

        if (Plugin.Config.EnableLocalServer)
            LocalBroadcastServer.Start(Plugin.Config.LocalServerPort);

        BroadcastScheduler.Start(NGame.Instance, Plugin.Config);
        MainMenuTwitchButton.SetupIfNeeded(__instance);
        Logging.Log("Twitch Overlay Mod initialized.");

        if (Plugin.Config.EnableBackfill && Plugin.Backfill == null)
        {
            var backfill = new BackfillManager();
            backfill.Scan();
            backfill.Load();
            backfill.AssignIds();
            Plugin.SetBackfill(backfill);
            BroadcastScheduler.SetBackfill(backfill);
            Logging.Log("Backfill manager initialized.");
        }
    }
}
