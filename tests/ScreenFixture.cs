using System.Collections.Immutable;
using NovellMaker.Core.Screens;

namespace NovellMaker;

/// <summary>Test-owned geometry. This is not an engine screen or a production factory.</summary>
public static class ScreenFixture
{
    public static void RemoveGeometry(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            if (obj.ContainsKey("nodeId") && obj.ContainsKey("action")) { obj.Remove("slotId"); obj.Remove("slotTitle"); }
            if (obj.ContainsKey("bounds") && obj.ContainsKey("parentId"))
                foreach (var key in new[] { "geometry", "clipChildren", "order", "color", "dialogue" }) obj.Remove(key);
            foreach (var value in obj.ToArray()) if (value.Value is { } child) RemoveGeometry(child);
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
            foreach (var value in array) if (value is { } child) RemoveGeometry(child);
    }
    public static ScreenAsset Create(string name)
    {
        var root = Guid.NewGuid(); var title = Guid.NewGuid(); var resume = Guid.NewGuid(); var other = Guid.NewGuid();
        return new(Guid.NewGuid(), name, [
            new(root, null, ScreenNodeKind.Panel, new(360, 200, 560, 300)),
            new(title, root, ScreenNodeKind.Text, new(40, 24, 480, 50), "Пауза"),
            new(resume, root, ScreenNodeKind.Button, new(40, 100, 480, 52), "Продолжить"),
            new(other, root, ScreenNodeKind.Button, new(40, 170, 480, 52), "Другая сцена")])
        {
            References = ImmutableDictionary<string, Guid>.Empty.Add("title", title).Add("continue_button", resume),
            Buttons = [new(resume, ScreenButtonAction.Close, null), new(other, ScreenButtonAction.None, null)]
        };
    }
}
