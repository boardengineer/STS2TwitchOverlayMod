namespace TwitchOverlayMod.Utility;

internal static class Logging
{
    internal static void Log(string message)
    {
        MegaCrit.Sts2.Core.Logging.Log.Info($"[TwitchOverlayMod] {message}");
    }
}
