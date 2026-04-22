using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using TwitchOverlayMod.Models;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.State;

internal static class MapSnapshotCache
{
    private static readonly FieldInfo? MapPointDictionaryField =
        typeof(NMapScreen).GetField("_mapPointDictionary", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly Dictionary<int, List<MapPointInfo>> _snapshotsByAct = new();

    internal static void Capture(NMapScreen screen)
    {
        if (MapPointDictionaryField?.GetValue(screen)
            is not Dictionary<MapCoord, NMapPoint> dictionary) return;

        var actIndex = RunManager.Instance?.DebugOnlyGetState()?.CurrentActIndex ?? -1;
        if (actIndex < 0) return;

        var points = new List<MapPointInfo>(dictionary.Count);
        foreach (var (coord, nMapPoint) in dictionary)
        {
            var model = nMapPoint.Point;
            var info = new MapPointInfo
            {
                Col = coord.col,
                Row = coord.row,
                X = nMapPoint.Position.X,
                Y = nMapPoint.Position.Y,
                Type = (int)model.PointType
            };

            foreach (var child in model.Children)
                info.Children.Add([child.coord.col, child.coord.row]);

            points.Add(info);
        }

        _snapshotsByAct[actIndex] = points;
        Logging.Log($"Map snapshot captured for act {actIndex}: {points.Count} points.");
    }

    internal static bool TryGetSnapshot(int actIndex, out List<MapPointInfo> points)
    {
        return _snapshotsByAct.TryGetValue(actIndex, out points!);
    }

    internal static Dictionary<(int col, int row), int>? ReadLiveStates()
    {
        var screen = NRun.Instance?.GlobalUi.MapScreen;
        if (screen == null) return null;
        if (MapPointDictionaryField?.GetValue(screen)
            is not Dictionary<MapCoord, NMapPoint> dictionary) return null;

        var states = new Dictionary<(int col, int row), int>(dictionary.Count);
        foreach (var (coord, nMapPoint) in dictionary)
            states[(coord.col, coord.row)] = (int)nMapPoint.State;
        return states;
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
internal class MapSnapshotCaptureSetMapPatch
{
    [HarmonyPostfix]
    public static void Postfix(NMapScreen __instance)
    {
        MapSnapshotCache.Capture(__instance);
    }
}
