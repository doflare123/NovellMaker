using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class LuaLimitBoundaryTests
{
    [TestMethod]
    public void VariableLimitsAcceptTheBoundaryAndRejectTheNextCharacterOrEntry()
    {
        Assert.IsTrue(LuaLimits.ValidVariable(new string('n', 128), new string('v', 8192)));
        Assert.IsFalse(LuaLimits.ValidVariable(new string('n', 129), ""));
        Assert.IsFalse(LuaLimits.ValidVariable("n", new string('v', 8193)));
        Assert.IsFalse(LuaLimits.ValidVariable(" ", "value"));
        Assert.IsFalse(LuaLimits.ValidVariable("name", null!));
        var values = Enumerable.Range(0, 257).Select(i => KeyValuePair.Create("v" + i, "")).ToArray();
        Assert.IsTrue(LuaLimits.ValidVariables(values.Take(256)));
        Assert.IsFalse(LuaLimits.ValidVariables(values));

        var characters = Enumerable.Range(0, 16).Select(i => KeyValuePair.Create(i.ToString("D2"), new string('x', i == 15 ? 8160 : 8192))).ToArray();
        Assert.AreEqual(LuaLimits.MaximumVariableCharacters, characters.Sum(p => p.Key.Length + p.Value.Length));
        Assert.IsTrue(LuaLimits.ValidVariables(characters));
        characters[^1] = KeyValuePair.Create(characters[^1].Key, characters[^1].Value + "x");
        Assert.IsFalse(LuaLimits.ValidVariables(characters));
    }

    [TestMethod]
    public void QueueLimitsCountBothCommandsAndPayloadCharacters()
    {
        var commands = Enumerable.Range(0, 4097).Select(_ => (ScenarioAction)new ShowAction(Guid.NewGuid(), Guid.NewGuid())).ToArray();
        Assert.IsTrue(LuaLimits.ValidQueue(commands.Take(4096)));
        Assert.IsFalse(LuaLimits.ValidQueue(commands));
        var payload = Enumerable.Range(0, 64).Select(_ => (ScenarioAction)new SetAction(Guid.NewGuid(), "v", new string('x', 8191))).ToArray();
        Assert.IsTrue(LuaLimits.ValidQueue(payload));
        Assert.IsFalse(LuaLimits.ValidQueue(payload.Append(new WaitAction(Guid.NewGuid(), "x"))));
        Assert.IsFalse(LuaLimits.ValidQueue([null!]));
        Assert.IsFalse(LuaLimits.ValidQueue([new SetAction(Guid.NewGuid(), "v", null!)]));
    }

    [TestMethod]
    public void SourcePreflightBoundariesAreIndependentOfMoonSharpParsing()
    {
        LuaLimits.CheckSource(new string(' ', LuaActionScript.MaximumSourceLength));
        Assert.Throws<ArgumentException>(() => LuaLimits.CheckSource(new string(' ', LuaActionScript.MaximumSourceLength + 1)));
        Assert.Throws<ArgumentException>(() => LuaLimits.CheckSource(null!));
        LuaLimits.CheckSource(new string('(', 64) + "1" + new string(')', 64));
        Assert.Throws<ArgumentException>(() => LuaLimits.CheckSource(new string('(', 65) + "1" + new string(')', 65)));
        LuaLimits.CheckSource(string.Concat(Enumerable.Repeat("do end ", 128)));
        Assert.Throws<ArgumentException>(() => LuaLimits.CheckSource(string.Concat(Enumerable.Repeat("do end ", 129))));
        LuaLimits.CheckSource(new string('+', 256));
        Assert.Throws<ArgumentException>(() => LuaLimits.CheckSource(new string('+', 257)));
        LuaLimits.CheckSource(string.Concat(Enumerable.Repeat("a ", 4096)));
        Assert.Throws<ArgumentException>(() => LuaLimits.CheckSource(string.Concat(Enumerable.Repeat("a ", 4097))));
    }
}
