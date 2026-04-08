using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using TwitchOverlayMod.Config;
using TwitchOverlayMod.Scheduling;
using TwitchOverlayMod.State;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod;

[ModInitializer("Initialize")]
public class Plugin
{
    internal static ModConfig? Config { get; private set; }

    public static void Initialize()
    {
        Config = ModConfig.Load();
        if (Config == null)
        {
            Logging.Log("Config not found — place TwitchOverlayMod.config.json next to the DLL.");
            return;
        }

        Logging.Log("Config loaded successfully.");

        CardIdMapper.Load();
        Logging.Log("Card ID map loaded.");

        EnemyIdMapper.Load();
        Logging.Log("Enemy ID map loaded.");

        RelicIdMapper.Load();
        Logging.Log("Relic ID map loaded.");

        var harmony = new Harmony("com.author.twitchoverlaymod");
        harmony.PatchAll(typeof(Plugin).Assembly);
    }
}

[HarmonyPatch(typeof(NGame), "LoadMainMenu")]
public class MainMenuPatch
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        if (Plugin.Config == null) return;

        BroadcastScheduler.Start(NGame.Instance!, Plugin.Config);
        Logging.Log("Twitch Overlay Mod initialized.");
    }
}
