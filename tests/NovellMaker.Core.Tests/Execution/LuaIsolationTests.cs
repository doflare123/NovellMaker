using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class LuaIsolationTests
{
    [TestMethod]
    public void AuditNestedParenthesesAreRejectedWithoutCrashingCaller()
    {
        var source = "return " + new string('(', 12000) + "1" + new string(')', 12000);
        Assert.AreEqual(24008, source.Length);
        var error = Assert.Throws<ArgumentException>(() => LuaActionScript.ValidateSyntax(source, "audit.lua"));
        StringAssert.Contains(error.Message, "вложенности");
        LuaActionScript.ValidateSyntax("return 1", "after.lua");
    }

    [TestMethod]
    [DataRow("blocks")]
    [DataRow("unary")]
    [DataRow("operators")]
    public void AdditionalRecursiveSyntaxShapesHaveBoundedComplexity(string kind)
    {
        var source = kind switch
        {
            "blocks" => string.Concat(Enumerable.Repeat("do ", 200)) + string.Concat(Enumerable.Repeat("end ", 200)),
            "unary" => "return " + string.Concat(Enumerable.Repeat("not ", 1000)) + "true",
            _ => "return " + string.Join("..", Enumerable.Repeat("'a'", 2000))
        };
        Assert.Throws<ArgumentException>(() => LuaActionScript.ValidateSyntax(source, "deep.lua"));
    }

    [TestMethod]
    public void StringsAndLongCommentsDoNotCountAsSyntaxNesting()
    {
        var payload = new string('(', 10000);
        LuaActionScript.ValidateSyntax("--[==[" + payload + "]==]\nreturn [=[" + payload + "]=]", "literal.lua");
    }

    [TestMethod]
    public void AuditMemoryBombDiesInWorkerAndLeavesCallerAndStateIntact()
    {
        var (project, scene, action) = Fixture("game.set('partial','bad'); local s='x'; for i=1,28 do s=s..s end; game.set('result',s)");
        var player = new ScenarioPlayer(project, scene.Id, "ru");
        Assert.IsNotNull(player.State.Error);
        Assert.IsEmpty(player.State.Variables);
        Assert.AreEqual(action.Id, player.State.NextActionId, "A rejected batch must not advance the script position.");
        StringAssert.Contains(player.State.Error, "Изолированный");
        LuaActionScript.ValidateSyntax("game.set('alive','yes')", "caller-alive.lua");
    }

    [TestMethod]
    public void ResultAndCumulativeVariableQuotasRejectWholeBatch()
    {
        foreach (var source in new[]
        {
            "game.set('partial','bad'); local s='x'; for i=1,14 do s=s..s end; game.set('large',s)",
            "game.set('partial','bad'); local s='x'; for i=1,13 do s=s..s end; for i=1,20 do game.set('v'..i,s) end"
        })
        {
            var (project, scene, action) = Fixture(source);
            var player = new ScenarioPlayer(project, scene.Id, "ru");
            Assert.IsNotNull(player.State.Error); Assert.IsEmpty(player.State.Variables);
            Assert.AreEqual(action.Id, player.State.NextActionId);
        }
    }

    private static (NovelProject, NovelScene, ScriptAction) Fixture(string source)
    {
        var action = new ScriptAction(Guid.NewGuid(), source, "audit-memory.lua");
        var scene = NovelScene.Create("Audit") with { UseActions = true, Actions = [action] };
        return (NovelProject.Create("Audit") with { Scenes = [scene] }, scene, action);
    }
}
