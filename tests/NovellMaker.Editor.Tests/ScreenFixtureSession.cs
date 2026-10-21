using NovellMaker.Core.Screens;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

internal static class ScreenFixtureSession
{
    internal static void AddTestScreen(this EditorSession session)
    {
        if (!session.CanEdit || session.Project is null) return;
        var fixture = ScreenFixture.Create("Тестовое меню");
        session.AddScene(fixture.ToScene(), session.Project.InputBindings.Any(b => b.Key == "P") ? [] : [new("P", ScreenButtonAction.Toggle, fixture.Id)]);
    }
    internal static void SetTestShortcut(this EditorSession session, Guid? id)
    {
        if (id is null) session.RemoveInputBinding("P");
        else if (session.Project?.Scenes.Any(s => s.Id == id) == true) session.SetInputBinding(new("P", ScreenButtonAction.Toggle, id));
    }
}
