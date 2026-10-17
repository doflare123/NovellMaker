namespace NovellMaker.Core.Styling;

public sealed record DialogueStyle
{
    internal DialogueStyle(
        string backgroundColor,
        string textColor,
        string borderColor,
        string fontFamily,
        string textAlign,
        double fontSize,
        double paddingTop,
        double paddingRight,
        double paddingBottom,
        double paddingLeft,
        double borderWidth,
        double borderRadius,
        double width,
        double minHeight,
        double left,
        double bottom,
        DialogueAnimation? animation)
    {
        BackgroundColor = backgroundColor;
        TextColor = textColor;
        BorderColor = borderColor;
        FontFamily = fontFamily;
        TextAlign = textAlign;
        FontSize = fontSize;
        PaddingTop = paddingTop;
        PaddingRight = paddingRight;
        PaddingBottom = paddingBottom;
        PaddingLeft = paddingLeft;
        BorderWidth = borderWidth;
        BorderRadius = borderRadius;
        Width = width;
        MinHeight = minHeight;
        Left = left;
        Bottom = bottom;
        Animation = animation;
    }

    public string BackgroundColor { get; }

    public string TextColor { get; }

    public string BorderColor { get; }

    public string FontFamily { get; }

    public string TextAlign { get; }

    public double FontSize { get; }

    public double PaddingTop { get; }

    public double PaddingRight { get; }

    public double PaddingBottom { get; }

    public double PaddingLeft { get; }

    public double BorderWidth { get; }

    public double BorderRadius { get; }

    public double Width { get; }

    public double MinHeight { get; }

    public double Left { get; }

    public double Bottom { get; }

    public string? AnimationName => Animation?.Name;

    public TimeSpan AnimationDuration => Animation?.Duration ?? TimeSpan.Zero;

    public string AnimationTimingFunction => Animation?.TimingFunction ?? "linear";

    public double? AnimationIterationCount => Animation?.IterationCount;

    public bool HasAnimation => Animation is not null;

    internal DialogueAnimation? Animation { get; }

    public DialogueAnimationSample Sample(TimeSpan elapsed)
    {
        if (Animation is null)
        {
            return DialogueAnimationSample.Default;
        }

        return Animation.Sample(elapsed);
    }
}

public sealed record DialogueAnimationSample(double Opacity, double TranslateX, double TranslateY, double Scale)
{
    public static DialogueAnimationSample Default { get; } = new(1, 0, 0, 1);
}

internal sealed record DialogueAnimation(
    string Name,
    TimeSpan Duration,
    string TimingFunction,
    double? IterationCount,
    IReadOnlyList<DialogueAnimationKeyframe> Keyframes)
{
    public DialogueAnimationSample Sample(TimeSpan elapsed)
    {
        var durationSeconds = Duration.TotalSeconds;
        var elapsedSeconds = Math.Max(0, elapsed.TotalSeconds);
        double progress;

        if (durationSeconds <= 0)
        {
            progress = 1;
        }
        else if (IterationCount is double finiteCount)
        {
            var totalDuration = durationSeconds * finiteCount;
            if (elapsedSeconds >= totalDuration)
            {
                progress = finiteCount % 1;
                if (progress == 0)
                {
                    progress = 1;
                }
            }
            else
            {
                progress = elapsedSeconds % durationSeconds / durationSeconds;
            }
        }
        else
        {
            progress = elapsedSeconds % durationSeconds / durationSeconds;
        }

        var upperIndex = 0;
        while (upperIndex < Keyframes.Count && Keyframes[upperIndex].Offset < progress)
        {
            upperIndex++;
        }

        if (upperIndex == 0)
        {
            return Keyframes[0].Sample;
        }

        if (upperIndex >= Keyframes.Count)
        {
            return Keyframes[^1].Sample;
        }

        var lower = Keyframes[upperIndex - 1];
        var upper = Keyframes[upperIndex];
        var interval = upper.Offset - lower.Offset;
        var localProgress = interval <= 0 ? 1 : (progress - lower.Offset) / interval;
        localProgress = ApplyTiming(localProgress);

        return new DialogueAnimationSample(
            Lerp(lower.Sample.Opacity, upper.Sample.Opacity, localProgress),
            Lerp(lower.Sample.TranslateX, upper.Sample.TranslateX, localProgress),
            Lerp(lower.Sample.TranslateY, upper.Sample.TranslateY, localProgress),
            Lerp(lower.Sample.Scale, upper.Sample.Scale, localProgress));
    }

    private double ApplyTiming(double progress) => TimingFunction switch
    {
        "ease-in" => progress * progress,
        "ease-out" => 1 - ((1 - progress) * (1 - progress)),
        "ease-in-out" => progress < 0.5
            ? 2 * progress * progress
            : 1 - (Math.Pow(-2 * progress + 2, 2) / 2),
        _ => progress,
    };

    private static double Lerp(double from, double to, double progress) =>
        from + ((to - from) * progress);
}

internal sealed record DialogueAnimationKeyframe(double Offset, DialogueAnimationSample Sample);
