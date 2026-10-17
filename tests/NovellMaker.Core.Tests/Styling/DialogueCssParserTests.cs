using System.Globalization;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Tests.Styling;

[TestClass]
public sealed class DialogueCssParserTests
{
    [TestMethod]
    public void Parse_EmptyStyle_UsesDocumentedDefaults()
    {
        var style = DialogueCssParser.Parse("  \r\n ");

        Assert.AreEqual("#202226EE", style.BackgroundColor);
        Assert.AreEqual("#E6E7E9", style.TextColor);
        Assert.AreEqual("Inter", style.FontFamily);
        Assert.AreEqual(22, style.FontSize);
        Assert.AreEqual(24, style.PaddingTop);
        Assert.AreEqual(960, style.Width);
        Assert.AreEqual(150, style.MinHeight);
        Assert.AreEqual(DialogueAnimationSample.Default, style.Sample(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public void Parse_AppliesSupportedPropertiesAndCssPaddingOrder()
    {
        const string css = """
            /* Project dialogue theme */
            .dialogue {
              background-color: rgba(10, 20, 30, 0.5);
              color: #abc;
              border-color: #01020304;
              padding: 10px 20px 30px 40px;
              border-width: 2px;
              border-radius: 12px;
              font-size: 28px;
              font-family: "Noto Sans";
              text-align: center;
              width: 1200px;
              min-height: 180px;
              left: 44px;
              bottom: 50px
            }
            """;

        var style = DialogueCssParser.Parse(css);

        Assert.AreEqual("#0A141E80", style.BackgroundColor);
        Assert.AreEqual("#AABBCC", style.TextColor);
        Assert.AreEqual("#01020304", style.BorderColor);
        Assert.AreEqual(10, style.PaddingTop);
        Assert.AreEqual(20, style.PaddingRight);
        Assert.AreEqual(30, style.PaddingBottom);
        Assert.AreEqual(40, style.PaddingLeft);
        Assert.AreEqual(2, style.BorderWidth);
        Assert.AreEqual(12, style.BorderRadius);
        Assert.AreEqual(28, style.FontSize);
        Assert.AreEqual("Noto Sans", style.FontFamily);
        Assert.AreEqual("center", style.TextAlign);
        Assert.AreEqual(1200, style.Width);
        Assert.AreEqual(180, style.MinHeight);
        Assert.AreEqual(44, style.Left);
        Assert.AreEqual(50, style.Bottom);
    }

    [TestMethod]
    public void Parse_TwoValuePadding_UsesVerticalAndHorizontalValues()
    {
        var style = DialogueCssParser.Parse(".dialogue { padding: 8px 16px; }");

        Assert.AreEqual(8, style.PaddingTop);
        Assert.AreEqual(16, style.PaddingRight);
        Assert.AreEqual(8, style.PaddingBottom);
        Assert.AreEqual(16, style.PaddingLeft);
    }

    [TestMethod]
    public void Parse_IsCultureInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");

            var style = DialogueCssParser.Parse(".dialogue { border-width: 1.5px; }");

            Assert.AreEqual(1.5, style.BorderWidth);
            Assert.ThrowsExactly<DialogueCssException>(() =>
                DialogueCssParser.Parse(".dialogue { border-width: 1,5px; }"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [TestMethod]
    public void Parse_ReportsLineAndColumnForUnknownProperty()
    {
        var exception = Assert.ThrowsExactly<DialogueCssException>(() =>
            DialogueCssParser.Parse("""
                .dialogue {
                  box-shadow: none;
                }
                """));

        Assert.AreEqual(2, exception.Line);
        Assert.AreEqual(3, exception.Column);
        StringAssert.Contains(exception.Message, "box-shadow");
        StringAssert.Contains(exception.Message, "строка 2");
    }

    [TestMethod]
    [DataRow("body { color: #fff; }")]
    [DataRow(".dialogue:hover { color: #fff; }")]
    [DataRow("@import 'theme.css'; .dialogue { color: #fff; }")]
    [DataRow(".dialogue { background-color: url(file.png); }")]
    [DataRow(".dialogue { animation: fade 1s linear; }")]
    [DataRow(".dialogue { width: calc(100px + 2px); }")]
    [DataRow(".dialogue { color: red; }")]
    public void Parse_RejectsUnsupportedOrExternalCss(string css)
    {
        Assert.ThrowsExactly<DialogueCssException>(() => DialogueCssParser.Parse(css));
    }

    [TestMethod]
    [DataRow(".dialogue { font-size: 7px; }")]
    [DataRow(".dialogue { font-size: 161px; }")]
    [DataRow(".dialogue { width: 8193px; }")]
    [DataRow(".dialogue { left: -1px; }")]
    [DataRow(".dialogue { padding: 1em; }")]
    [DataRow(".dialogue { background-color: rgba(256, 0, 0, 1); }")]
    public void Parse_RejectsMalformedOrOutOfRangeValues(string css)
    {
        Assert.ThrowsExactly<DialogueCssException>(() => DialogueCssParser.Parse(css));
    }

    [TestMethod]
    public void Parse_RejectsOversizedInputBeforeParsing()
    {
        var css = new string(' ', DialogueCssParser.MaximumInputLength + 1);

        var exception = Assert.ThrowsExactly<DialogueCssException>(() => DialogueCssParser.Parse(css));

        Assert.AreEqual(1, exception.Line);
        Assert.AreEqual(1, exception.Column);
    }

    [TestMethod]
    public void Animation_InterpolatesOpacityTranslationAndScale()
    {
        var style = ParseAnimation("2s", "linear", "1", """
            from { opacity: 0; transform: translateX(-20px) translateY(10px) scale(0.5); }
            to { opacity: 1; transform: translateX(20px) translateY(-10px) scale(1.5); }
            """);

        var start = style.Sample(TimeSpan.Zero);
        var middle = style.Sample(TimeSpan.FromSeconds(1));
        var end = style.Sample(TimeSpan.FromSeconds(2));

        AssertSample(start, 0, -20, 10, 0.5);
        AssertSample(middle, 0.5, 0, 0, 1);
        AssertSample(end, 1, 20, -10, 1.5);
    }

    [TestMethod]
    public void Animation_UsesIntermediatePercentageKeyframes()
    {
        var style = ParseAnimation("4s", "linear", "1", """
            0% { opacity: 0; }
            25% { opacity: 1; }
            100% { opacity: 0.5; }
            """);

        Assert.AreEqual(1, style.Sample(TimeSpan.FromSeconds(1)).Opacity, 0.000001);
        Assert.AreEqual(0.75, style.Sample(TimeSpan.FromSeconds(2.5)).Opacity, 0.000001);
    }

    [TestMethod]
    public void Animation_FiniteAnimationFreezesAtFinalKeyframe()
    {
        var style = ParseAnimation("500ms", "linear", "2", """
            from { opacity: 0; }
            to { opacity: 1; }
            """);

        Assert.AreEqual(0.5, style.Sample(TimeSpan.FromMilliseconds(750)).Opacity, 0.000001);
        Assert.AreEqual(1, style.Sample(TimeSpan.FromSeconds(100)).Opacity, 0.000001);
    }

    [TestMethod]
    public void Animation_InfiniteAnimationLoops()
    {
        var style = ParseAnimation("1s", "linear", "infinite", """
            from { transform: translateY(0px); }
            to { transform: translateY(100px); }
            """);

        Assert.AreEqual(25, style.Sample(TimeSpan.FromMilliseconds(250)).TranslateY, 0.000001);
        Assert.AreEqual(25, style.Sample(TimeSpan.FromMilliseconds(2250)).TranslateY, 0.000001);
        Assert.IsNull(style.AnimationIterationCount);
    }

    [TestMethod]
    public void Animation_ZeroDurationImmediatelyUsesFinalKeyframe()
    {
        var style = ParseAnimation("0s", "ease-in", "1", """
            from { opacity: 0; }
            to { opacity: 0.75; }
            """);

        Assert.AreEqual(0.75, style.Sample(TimeSpan.Zero).Opacity, 0.000001);
    }

    [TestMethod]
    public void Animation_EasingIsDeterministic()
    {
        var style = ParseAnimation("1s", "ease-in", "1", """
            from { opacity: 0; }
            to { opacity: 1; }
            """);

        Assert.AreEqual(0.25, style.Sample(TimeSpan.FromMilliseconds(500)).Opacity, 0.000001);
    }

    [TestMethod]
    [DataRow("animation-duration: 61s;")]
    [DataRow("animation-duration: -1s;")]
    [DataRow("animation-timing-function: ease;")]
    [DataRow("animation-iteration-count: 0;")]
    [DataRow("animation-iteration-count: forever;")]
    public void Animation_RejectsInvalidOptions(string option)
    {
        var css = $".dialogue {{ animation-name: fade; {option} }} @keyframes fade {{ from {{ opacity: 0; }} to {{ opacity: 1; }} }}";

        Assert.ThrowsExactly<DialogueCssException>(() => DialogueCssParser.Parse(css));
    }

    [TestMethod]
    public void Animation_RejectsUnsupportedKeyframePropertyWithLocation()
    {
        var exception = Assert.ThrowsExactly<DialogueCssException>(() =>
            DialogueCssParser.Parse("""
                .dialogue { animation-name: fade; }
                @keyframes fade {
                  from { color: #fff; }
                  to { opacity: 1; }
                }
                """));

        Assert.AreEqual(3, exception.Line);
        Assert.IsTrue(exception.Column > 0);
        StringAssert.Contains(exception.Message, "color");
    }

    [TestMethod]
    public void Animation_RejectsMismatchedAnimationName()
    {
        Assert.ThrowsExactly<DialogueCssException>(() => DialogueCssParser.Parse("""
            .dialogue { animation-name: slide; }
            @keyframes fade { from { opacity: 0; } to { opacity: 1; } }
            """));
    }

    private static DialogueStyle ParseAnimation(
        string duration,
        string timing,
        string iterations,
        string keyframes) => DialogueCssParser.Parse($$"""
            .dialogue {
              animation-name: reveal;
              animation-duration: {{duration}};
              animation-timing-function: {{timing}};
              animation-iteration-count: {{iterations}};
            }
            @keyframes reveal {
              {{keyframes}}
            }
            """);

    private static void AssertSample(
        DialogueAnimationSample actual,
        double opacity,
        double translateX,
        double translateY,
        double scale)
    {
        Assert.AreEqual(opacity, actual.Opacity, 0.000001);
        Assert.AreEqual(translateX, actual.TranslateX, 0.000001);
        Assert.AreEqual(translateY, actual.TranslateY, 0.000001);
        Assert.AreEqual(scale, actual.Scale, 0.000001);
    }
}
