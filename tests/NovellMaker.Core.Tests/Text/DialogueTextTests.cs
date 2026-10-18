using NovellMaker.Core.Text;

namespace NovellMaker.Core.Tests.Text;

[TestClass]
public sealed class DialogueTextTests
{
    [TestMethod]
    public void Parse_SplitsOnlyAtSupportedColorAndResetCodes()
    {
        var runs = DialogueText.Parse("Обычно §#12abEFцветом§r снова обычно");

        AssertRuns(
            runs,
            ("Обычно ", null),
            ("цветом", "#12ABEF"),
            (" снова обычно", null));
    }

    [TestMethod]
    public void Parse_ChangesColorWithoutAddingEmptyRuns()
    {
        var runs = DialogueText.Parse("§#ff0000Красный§#00ff00Зелёный§r");

        AssertRuns(runs, ("Красный", "#FF0000"), ("Зелёный", "#00FF00"));
    }

    [TestMethod]
    public void Parse_EscapedSectionSignRemainsTextUnderCurrentColor()
    {
        var runs = DialogueText.Parse("§#abcdefЦена §§100§r");

        AssertRuns(runs, ("Цена §100", "#ABCDEF"));
    }

    [TestMethod]
    [DataRow("§a")]
    [DataRow("§#12G456")]
    [DataRow("§#12345")]
    [DataRow("текст§")]
    [DataRow("§x неизвестно")]
    public void Parse_PreservesMalformedAndUnsupportedSequencesLiterally(string source)
    {
        var runs = DialogueText.Parse(source);

        AssertRuns(runs, (source, null));
    }

    [TestMethod]
    public void Parse_PreservesUnicodeNewlinesAndEmoji()
    {
        const string source = "Первая строка\nВторая: Ёжик 🌙";

        var runs = DialogueText.Parse(source);

        AssertRuns(runs, (source, null));
    }

    [TestMethod]
    public void Parse_FormattingCodesAtEdgesDoNotProduceEmptyRuns()
    {
        var colored = DialogueText.Parse("§#112233Текст§r");
        var codesOnly = DialogueText.Parse("§#112233§r");

        AssertRuns(colored, ("Текст", "#112233"));
        Assert.HasCount(0, codesOnly);
    }

    [TestMethod]
    public void Parse_RejectsNullAtContractBoundary()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => DialogueText.Parse(null!));
    }

    private static void AssertRuns(
        IReadOnlyList<TextRun> actual,
        params (string Text, string? Color)[] expected)
    {
        Assert.AreEqual(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index].Text, actual[index].Text, $"Текст сегмента {index} отличается.");
            Assert.AreEqual(expected[index].Color, actual[index].Color, $"Цвет сегмента {index} отличается.");
            Assert.IsFalse(string.IsNullOrEmpty(actual[index].Text), $"Сегмент {index} не должен быть пустым.");
        }
    }
}
