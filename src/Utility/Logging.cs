using Godot;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace TwitchOverlayMod.Utility;

internal static class Logging
{
    internal static void Log(string message)
    {
        var console = NDevConsole.Instance;
        var outputBuffer = console.GetNode<RichTextLabel>("OutputContainer/OutputBuffer");
        outputBuffer.Text += $"[TwitchOverlayMod] {message}\n";
    }
}
