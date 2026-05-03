using System.Diagnostics;

namespace TwitchOverlayMod.Utility;

internal static class Logging
{
    [Conditional("VERBOSE_LOGGING")]
    internal static void Log(string message)
    {
        MegaCrit.Sts2.Core.Logging.Log.Info($"[TwitchOverlayMod] {message}");
    }
}
