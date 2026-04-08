using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace TwitchOverlayMod.Utility;

internal static class Logging
{
    internal static void Log(string message)
    {
        var formatted = $"[TwitchOverlayMod] {message}";
        MegaCrit.Sts2.Core.Logging.Log.Info(formatted);

        try
        {
            var console = NDevConsole.Instance;
            var outputBuffer = console.GetNode<RichTextLabel>("OutputContainer/OutputBuffer");
            outputBuffer.Text += formatted + "\n";
        }
        catch (InvalidOperationException)
        {
            // Dev console not created yet — game log only
        }
    }
}
