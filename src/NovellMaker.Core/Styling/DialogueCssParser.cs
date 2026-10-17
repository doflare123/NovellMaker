using System.Globalization;
using System.Text.RegularExpressions;

namespace NovellMaker.Core.Styling;

/// <summary>
/// Parses the deliberately small, portable CSS subset used by the dialogue renderer.
/// This is not a browser CSS parser: selectors, cascade, external resources and arbitrary
/// CSS functions are rejected so the editor and runtime can render the same result.
/// </summary>
public static partial class DialogueCssParser
{
    public const int MaximumInputLength = 32 * 1024;

    public const string DefaultCss =
        ".dialogue {\n" +
        "  background-color: #202226EE;\n" +
        "  color: #E6E7E9;\n" +
        "  padding: 24px;\n" +
        "  border-color: #454950;\n" +
        "  border-width: 1px;\n" +
        "  border-radius: 8px;\n" +
        "  font-size: 22px;\n" +
        "  font-family: Inter;\n" +
        "  text-align: left;\n" +
        "  width: 960px;\n" +
        "  min-height: 150px;\n" +
        "  left: 32px;\n" +
        "  bottom: 32px;\n" +
        "}";

    public static DialogueStyle Parse(string css)
    {
        ArgumentNullException.ThrowIfNull(css);

        if (css.Length > MaximumInputLength)
        {
            throw new DialogueCssException(
                $"Стиль превышает допустимый размер {MaximumInputLength} символов",
                1,
                1);
        }

        if (string.IsNullOrWhiteSpace(css))
        {
            css = DefaultCss;
        }

        return new Parser(css).Parse();
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _index;
        private int _line = 1;
        private int _column = 1;

        private IReadOnlyDictionary<string, Declaration>? _dialogueDeclarations;
        private KeyframesRule? _keyframes;

        public Parser(string text)
        {
            _text = text;
        }

        public DialogueStyle Parse()
        {
            while (true)
            {
                SkipTrivia();
                if (End)
                {
                    break;
                }

                if (Current == '.')
                {
                    ParseDialogueRule();
                }
                else if (Current == '@')
                {
                    ParseKeyframesRule();
                }
                else
                {
                    throw Error("Ожидался селектор .dialogue или правило @keyframes");
                }
            }

            if (_dialogueDeclarations is null)
            {
                throw new DialogueCssException("Отсутствует обязательный блок .dialogue", 1, 1);
            }

            return BuildStyle(_dialogueDeclarations, _keyframes);
        }

        private void ParseDialogueRule()
        {
            var (line, column) = Position;
            if (_dialogueDeclarations is not null)
            {
                throw new DialogueCssException("Допустим только один блок .dialogue", line, column);
            }

            ExpectLiteral(".dialogue", "Поддерживается только селектор .dialogue");
            SkipTrivia();
            if (!End && Current != '{')
            {
                throw Error("Составные селекторы и псевдоклассы не поддерживаются");
            }

            Expect('{', "После .dialogue ожидалась открывающая фигурная скобка");
            _dialogueDeclarations = ParseDeclarations();
        }

        private void ParseKeyframesRule()
        {
            var (line, column) = Position;
            if (_keyframes is not null)
            {
                throw new DialogueCssException("Допустимо только одно правило @keyframes", line, column);
            }

            ExpectLiteral("@keyframes", "Поддерживается только правило @keyframes");
            RequireWhitespace("После @keyframes ожидалось имя анимации");
            SkipTrivia();
            var name = ReadIdentifier("Ожидалось имя анимации");
            SkipTrivia();
            Expect('{', "После имени анимации ожидалась открывающая фигурная скобка");

            var frames = new List<KeyframeDeclaration>();
            while (true)
            {
                SkipTrivia();
                if (End)
                {
                    throw Error("Правило @keyframes не закрыто");
                }

                if (Current == '}')
                {
                    Advance();
                    break;
                }

                var frameLine = _line;
                var frameColumn = _column;
                var selector = ReadUntilTriviaOr('{');
                var offset = ParseKeyframeOffset(selector, frameLine, frameColumn);
                SkipTrivia();
                Expect('{', "После позиции ключевого кадра ожидалась открывающая фигурная скобка");
                var declarations = ParseDeclarations();

                foreach (var property in declarations.Keys)
                {
                    if (property is not ("opacity" or "transform"))
                    {
                        var declaration = declarations[property];
                        throw new DialogueCssException(
                            $"Свойство '{property}' не поддерживается в ключевых кадрах",
                            declaration.Line,
                            declaration.Column);
                    }
                }

                if (frames.Any(frame => frame.Offset == offset))
                {
                    throw new DialogueCssException("Позиция ключевого кадра повторяется", frameLine, frameColumn);
                }

                frames.Add(new KeyframeDeclaration(offset, declarations, frameLine, frameColumn));
            }

            if (frames.Count == 0)
            {
                throw new DialogueCssException("Правило @keyframes не содержит ключевых кадров", line, column);
            }

            _keyframes = new KeyframesRule(name, frames, line, column);
        }

        private Dictionary<string, Declaration> ParseDeclarations()
        {
            var declarations = new Dictionary<string, Declaration>(StringComparer.OrdinalIgnoreCase);

            while (true)
            {
                SkipTrivia();
                if (End)
                {
                    throw Error("Блок объявлений не закрыт");
                }

                if (Current == '}')
                {
                    Advance();
                    return declarations;
                }

                var line = _line;
                var column = _column;
                var property = ReadIdentifier("Ожидалось имя свойства").ToLowerInvariant();
                SkipTrivia();
                Expect(':', $"После свойства '{property}' ожидалось двоеточие");
                SkipTrivia();
                var valueLine = _line;
                var valueColumn = _column;
                var value = ReadDeclarationValue().Trim();

                if (value.Length == 0)
                {
                    throw new DialogueCssException($"Для свойства '{property}' не задано значение", valueLine, valueColumn);
                }

                if (value.Contains("url(", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("@import", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DialogueCssException("Внешние ресурсы в стилях запрещены", valueLine, valueColumn);
                }

                if (!declarations.TryAdd(property, new Declaration(value, line, column, valueLine, valueColumn)))
                {
                    throw new DialogueCssException($"Свойство '{property}' указано повторно", line, column);
                }

                SkipTrivia();
                if (!End && Current == ';')
                {
                    Advance();
                }
                else if (End || Current != '}')
                {
                    throw Error($"После свойства '{property}' ожидалась точка с запятой");
                }
            }
        }

        private string ReadDeclarationValue()
        {
            var start = _index;
            var parenthesisDepth = 0;
            char? quote = null;

            while (!End)
            {
                var current = Current;
                if (quote is not null)
                {
                    if (current == '\\')
                    {
                        throw Error("Escape-последовательности в строках не поддерживаются");
                    }

                    if (current == quote)
                    {
                        quote = null;
                    }

                    Advance();
                    continue;
                }

                if (current is '\'' or '"')
                {
                    quote = current;
                    Advance();
                }
                else if (current == '(')
                {
                    parenthesisDepth++;
                    Advance();
                }
                else if (current == ')')
                {
                    if (parenthesisDepth == 0)
                    {
                        throw Error("Лишняя закрывающая круглая скобка");
                    }

                    parenthesisDepth--;
                    Advance();
                }
                else if (parenthesisDepth == 0 && current is ';' or '}')
                {
                    break;
                }
                else if (current == '{')
                {
                    throw Error("Вложенные CSS-блоки не поддерживаются");
                }
                else
                {
                    Advance();
                }
            }

            if (quote is not null)
            {
                throw Error("Строковое значение не закрыто");
            }

            if (parenthesisDepth != 0)
            {
                throw Error("Круглая скобка в значении не закрыта");
            }

            return _text[start.._index];
        }

        private string ReadIdentifier(string errorMessage)
        {
            var start = _index;
            while (!End && (char.IsAsciiLetterOrDigit(Current) || Current is '-' or '_'))
            {
                Advance();
            }

            if (_index == start || char.IsAsciiDigit(_text[start]))
            {
                throw Error(errorMessage);
            }

            return _text[start.._index];
        }

        private string ReadUntilTriviaOr(char terminal)
        {
            var start = _index;
            while (!End && !char.IsWhiteSpace(Current) && Current != terminal)
            {
                Advance();
            }

            return _text[start.._index];
        }

        private void SkipTrivia()
        {
            while (!End)
            {
                if (char.IsWhiteSpace(Current))
                {
                    Advance();
                    continue;
                }

                if (Current == '/' && Peek(1) == '*')
                {
                    var line = _line;
                    var column = _column;
                    Advance();
                    Advance();
                    while (!End && !(Current == '*' && Peek(1) == '/'))
                    {
                        Advance();
                    }

                    if (End)
                    {
                        throw new DialogueCssException("Комментарий не закрыт", line, column);
                    }

                    Advance();
                    Advance();
                    continue;
                }

                break;
            }
        }

        private void RequireWhitespace(string message)
        {
            if (End || (!char.IsWhiteSpace(Current) && !(Current == '/' && Peek(1) == '*')))
            {
                throw Error(message);
            }
        }

        private void ExpectLiteral(string expected, string message)
        {
            if (!_text.AsSpan(_index).StartsWith(expected, StringComparison.Ordinal))
            {
                throw Error(message);
            }

            for (var i = 0; i < expected.Length; i++)
            {
                Advance();
            }
        }

        private void Expect(char expected, string message)
        {
            if (End || Current != expected)
            {
                throw Error(message);
            }

            Advance();
        }

        private void Advance()
        {
            if (Current == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }

            _index++;
        }

        private char Peek(int offset) => _index + offset < _text.Length ? _text[_index + offset] : '\0';

        private DialogueCssException Error(string message) => new(message, _line, _column);

        private (int Line, int Column) Position => (_line, _column);

        private char Current => _text[_index];

        private bool End => _index >= _text.Length;
    }

    private static DialogueStyle BuildStyle(
        IReadOnlyDictionary<string, Declaration> declarations,
        KeyframesRule? keyframes)
    {
        var builder = new StyleBuilder();

        foreach (var (property, declaration) in declarations)
        {
            switch (property)
            {
                case "background-color":
                    builder.BackgroundColor = ParseColor(declaration);
                    break;
                case "color":
                    builder.TextColor = ParseColor(declaration);
                    break;
                case "border-color":
                    builder.BorderColor = ParseColor(declaration);
                    break;
                case "font-family":
                    builder.FontFamily = ParseFontFamily(declaration);
                    break;
                case "text-align":
                    builder.TextAlign = ParseKeyword(declaration, "left", "center", "right");
                    break;
                case "font-size":
                    builder.FontSize = ParsePixelLength(declaration, 8, 160, property);
                    break;
                case "padding":
                    ParsePadding(declaration, builder);
                    break;
                case "border-width":
                    builder.BorderWidth = ParsePixelLength(declaration, 0, 8192, property);
                    break;
                case "border-radius":
                    builder.BorderRadius = ParsePixelLength(declaration, 0, 8192, property);
                    break;
                case "width":
                    builder.Width = ParsePixelLength(declaration, 0, 8192, property);
                    break;
                case "min-height":
                    builder.MinHeight = ParsePixelLength(declaration, 0, 8192, property);
                    break;
                case "left":
                    builder.Left = ParsePixelLength(declaration, 0, 8192, property);
                    break;
                case "bottom":
                    builder.Bottom = ParsePixelLength(declaration, 0, 8192, property);
                    break;
                case "animation-name":
                    builder.AnimationName = ParseAnimationName(declaration);
                    builder.AnimationNameDeclaration = declaration;
                    break;
                case "animation-duration":
                    builder.AnimationDuration = ParseDuration(declaration);
                    builder.FirstAnimationOption ??= declaration;
                    break;
                case "animation-timing-function":
                    builder.AnimationTimingFunction = ParseKeyword(
                        declaration,
                        "linear",
                        "ease-in",
                        "ease-out",
                        "ease-in-out");
                    builder.FirstAnimationOption ??= declaration;
                    break;
                case "animation-iteration-count":
                    builder.AnimationIterationSpecified = true;
                    var iterationCount = ParseIterationCount(declaration);
                    builder.AnimationInfinite = iterationCount is null;
                    builder.AnimationIterationCount = iterationCount;
                    builder.FirstAnimationOption ??= declaration;
                    break;
                default:
                    throw new DialogueCssException(
                        $"Свойство '{property}' не поддерживается",
                        declaration.Line,
                        declaration.Column);
            }
        }

        builder.Animation = BuildAnimation(builder, keyframes);
        return builder.Build();
    }

    private static DialogueAnimation? BuildAnimation(StyleBuilder builder, KeyframesRule? keyframes)
    {
        if (builder.AnimationName is null)
        {
            if (keyframes is not null)
            {
                throw new DialogueCssException(
                    $"Анимация '{keyframes.Name}' объявлена, но не назначена свойством animation-name",
                    keyframes.Line,
                    keyframes.Column);
            }

            if (builder.HasAnimationOptions)
            {
                throw new DialogueCssException(
                    "Для параметров анимации требуется свойство animation-name",
                    builder.FirstAnimationOption!.Line,
                    builder.FirstAnimationOption.Column);
            }

            return null;
        }

        if (builder.AnimationName == "none")
        {
            if (keyframes is not null)
            {
                throw new DialogueCssException(
                    "При animation-name: none правило @keyframes не используется",
                    keyframes.Line,
                    keyframes.Column);
            }

            return null;
        }

        var nameDeclaration = builder.AnimationNameDeclaration!;
        if (keyframes is null)
        {
            throw new DialogueCssException(
                $"Для анимации '{builder.AnimationName}' отсутствует правило @keyframes",
                nameDeclaration.ValueLine,
                nameDeclaration.ValueColumn);
        }

        if (!string.Equals(builder.AnimationName, keyframes.Name, StringComparison.Ordinal))
        {
            throw new DialogueCssException(
                $"Имя анимации '{builder.AnimationName}' не совпадает с @keyframes {keyframes.Name}",
                nameDeclaration.ValueLine,
                nameDeclaration.ValueColumn);
        }

        var frames = keyframes.Frames
            .Select(ParseKeyframe)
            .OrderBy(frame => frame.Offset)
            .ToList();

        if (frames[0].Offset > 0)
        {
            frames.Insert(0, new DialogueAnimationKeyframe(0, DialogueAnimationSample.Default));
        }

        if (frames[^1].Offset < 1)
        {
            frames.Add(new DialogueAnimationKeyframe(1, DialogueAnimationSample.Default));
        }

        return new DialogueAnimation(
            builder.AnimationName,
            builder.AnimationDuration ?? TimeSpan.Zero,
            builder.AnimationTimingFunction ?? "linear",
            builder.AnimationIterationSpecified
                ? builder.AnimationInfinite ? null : builder.AnimationIterationCount
                : 1,
            frames);
    }

    private static DialogueAnimationKeyframe ParseKeyframe(KeyframeDeclaration frame)
    {
        var opacity = 1d;
        var translateX = 0d;
        var translateY = 0d;
        var scale = 1d;

        if (frame.Declarations.TryGetValue("opacity", out var opacityDeclaration))
        {
            opacity = ParsePlainNumber(opacityDeclaration, 0, 1, "opacity");
        }

        if (frame.Declarations.TryGetValue("transform", out var transformDeclaration))
        {
            (translateX, translateY, scale) = ParseTransform(transformDeclaration);
        }

        return new DialogueAnimationKeyframe(
            frame.Offset,
            new DialogueAnimationSample(opacity, translateX, translateY, scale));
    }

    private static (double TranslateX, double TranslateY, double Scale) ParseTransform(Declaration declaration)
    {
        var value = declaration.Value.Trim();
        if (value == "none")
        {
            return (0, 0, 1);
        }

        var translateX = 0d;
        var translateY = 0d;
        var scale = 1d;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        while (index < value.Length)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index]))
            {
                index++;
            }

            if (index >= value.Length)
            {
                break;
            }

            var match = TransformFunctionRegex().Match(value, index);
            if (!match.Success || match.Index != index)
            {
                throw ValueError(declaration, "Поддерживаются только transform-функции translateX(px), translateY(px) и scale(number)");
            }

            var function = match.Groups[1].Value;
            if (!used.Add(function))
            {
                throw ValueError(declaration, $"Transform-функция '{function}' указана повторно");
            }

            var numberText = match.Groups[2].Value;
            var unit = match.Groups[3].Value;
            var number = ParseInvariant(numberText, declaration, "Некорректное число в transform");

            switch (function)
            {
                case "translateX" when unit == "px":
                    EnsureRange(number, -8192, 8192, declaration, "translateX");
                    translateX = number;
                    break;
                case "translateY" when unit == "px":
                    EnsureRange(number, -8192, 8192, declaration, "translateY");
                    translateY = number;
                    break;
                case "scale" when unit.Length == 0:
                    EnsureRange(number, 0, 100, declaration, "scale");
                    scale = number;
                    break;
                default:
                    throw ValueError(declaration, $"Недопустимая единица измерения для '{function}'");
            }

            index += match.Length;
        }

        if (used.Count == 0)
        {
            throw ValueError(declaration, "Значение transform пусто");
        }

        return (translateX, translateY, scale);
    }

    private static double ParseKeyframeOffset(string value, int line, int column)
    {
        if (value == "from")
        {
            return 0;
        }

        if (value == "to")
        {
            return 1;
        }

        var match = PercentageRegex().Match(value);
        if (!match.Success)
        {
            throw new DialogueCssException(
                "Позиция ключевого кадра должна быть from, to или процентом от 0% до 100%",
                line,
                column);
        }

        var number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        if (number is < 0 or > 100)
        {
            throw new DialogueCssException("Процент ключевого кадра должен быть от 0% до 100%", line, column);
        }

        return number / 100;
    }

    private static string ParseColor(Declaration declaration)
    {
        var value = declaration.Value.Trim();
        var hexMatch = HexColorRegex().Match(value);
        if (hexMatch.Success)
        {
            var hex = hexMatch.Groups[1].Value.ToUpperInvariant();
            return hex.Length switch
            {
                3 => $"#{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}",
                6 => $"#{hex}",
                _ => $"#{hex}",
            };
        }

        var rgbaMatch = RgbaColorRegex().Match(value);
        if (rgbaMatch.Success)
        {
            var red = ParseColorByte(rgbaMatch.Groups[1].Value, declaration);
            var green = ParseColorByte(rgbaMatch.Groups[2].Value, declaration);
            var blue = ParseColorByte(rgbaMatch.Groups[3].Value, declaration);
            var alpha = ParseInvariant(rgbaMatch.Groups[4].Value, declaration, "Некорректный альфа-канал rgba");
            EnsureRange(alpha, 0, 1, declaration, "Альфа-канал rgba");
            var alphaByte = (int)Math.Round(alpha * 255, MidpointRounding.AwayFromZero);
            return $"#{red:X2}{green:X2}{blue:X2}{alphaByte:X2}";
        }

        throw ValueError(declaration, "Цвет должен иметь формат #rgb, #rrggbb, #rrggbbaa или rgba(r,g,b,a)");
    }

    private static int ParseColorByte(string value, Declaration declaration)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result is < 0 or > 255)
        {
            throw ValueError(declaration, "Компоненты rgba должны быть целыми числами от 0 до 255");
        }

        return result;
    }

    private static void ParsePadding(Declaration declaration, StyleBuilder builder)
    {
        var parts = declaration.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4)
        {
            throw ValueError(declaration, "Padding принимает от одного до четырёх значений в px");
        }

        var values = parts.Select(part => ParsePixelLength(part, declaration, 0, 8192, "padding")).ToArray();
        (builder.PaddingTop, builder.PaddingRight, builder.PaddingBottom, builder.PaddingLeft) = values.Length switch
        {
            1 => (values[0], values[0], values[0], values[0]),
            2 => (values[0], values[1], values[0], values[1]),
            3 => (values[0], values[1], values[2], values[1]),
            _ => (values[0], values[1], values[2], values[3]),
        };
    }

    private static double ParsePixelLength(Declaration declaration, double minimum, double maximum, string property) =>
        ParsePixelLength(declaration.Value.Trim(), declaration, minimum, maximum, property);

    private static double ParsePixelLength(
        string value,
        Declaration declaration,
        double minimum,
        double maximum,
        string property)
    {
        var match = PixelLengthRegex().Match(value);
        if (!match.Success)
        {
            throw ValueError(declaration, $"Свойство '{property}' требует число в пикселях (px)");
        }

        var result = ParseInvariant(match.Groups[1].Value, declaration, $"Некорректное значение '{property}'");
        EnsureRange(result, minimum, maximum, declaration, property);
        return result;
    }

    private static double ParsePlainNumber(
        Declaration declaration,
        double minimum,
        double maximum,
        string property)
    {
        if (!PlainNumberRegex().IsMatch(declaration.Value.Trim()))
        {
            throw ValueError(declaration, $"Свойство '{property}' требует число без единицы измерения");
        }

        var result = ParseInvariant(declaration.Value.Trim(), declaration, $"Некорректное значение '{property}'");
        EnsureRange(result, minimum, maximum, declaration, property);
        return result;
    }

    private static TimeSpan ParseDuration(Declaration declaration)
    {
        var match = DurationRegex().Match(declaration.Value.Trim());
        if (!match.Success)
        {
            throw ValueError(declaration, "Длительность анимации задаётся в s или ms");
        }

        var value = ParseInvariant(match.Groups[1].Value, declaration, "Некорректная длительность анимации");
        var seconds = match.Groups[2].Value == "ms" ? value / 1000 : value;
        EnsureRange(seconds, 0, 60, declaration, "animation-duration");
        return TimeSpan.FromSeconds(seconds);
    }

    private static double? ParseIterationCount(Declaration declaration)
    {
        var value = declaration.Value.Trim();
        if (value == "infinite")
        {
            return null;
        }

        if (!PlainNumberRegex().IsMatch(value))
        {
            throw ValueError(declaration, "Число повторов должно быть положительным числом или infinite");
        }

        var count = ParseInvariant(value, declaration, "Некорректное число повторов");
        if (count <= 0 || count > 10_000)
        {
            throw ValueError(declaration, "Число повторов должно быть больше 0 и не больше 10000");
        }

        return count;
    }

    private static string ParseAnimationName(Declaration declaration)
    {
        var value = declaration.Value.Trim();
        if (value == "none")
        {
            return value;
        }

        if (!IdentifierRegex().IsMatch(value) || char.IsAsciiDigit(value[0]))
        {
            throw ValueError(declaration, "Некорректное имя анимации");
        }

        return value;
    }

    private static string ParseFontFamily(Declaration declaration)
    {
        var value = declaration.Value.Trim();
        if (value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0])
        {
            value = value[1..^1];
        }

        if (value.Length is < 1 or > 128 || value.Any(character =>
                !char.IsLetterOrDigit(character) && character is not (' ' or '-' or '_')))
        {
            throw ValueError(declaration, "Название шрифта содержит неподдерживаемые символы");
        }

        return value;
    }

    private static string ParseKeyword(Declaration declaration, params string[] allowed)
    {
        var value = declaration.Value.Trim();
        if (!allowed.Contains(value, StringComparer.Ordinal))
        {
            throw ValueError(declaration, $"Допустимые значения: {string.Join(", ", allowed)}");
        }

        return value;
    }

    private static double ParseInvariant(string value, Declaration declaration, string message)
    {
        if (!double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var result) || !double.IsFinite(result))
        {
            throw ValueError(declaration, message);
        }

        return result;
    }

    private static void EnsureRange(
        double value,
        double minimum,
        double maximum,
        Declaration declaration,
        string property)
    {
        if (value < minimum || value > maximum)
        {
            throw ValueError(declaration, $"Значение '{property}' должно быть от {minimum} до {maximum}");
        }
    }

    private static DialogueCssException ValueError(Declaration declaration, string message) =>
        new(message, declaration.ValueLine, declaration.ValueColumn);

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorRegex();

    [GeneratedRegex(@"^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*\)$")]
    private static partial Regex RgbaColorRegex();

    [GeneratedRegex(@"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))px$")]
    private static partial Regex PixelLengthRegex();

    [GeneratedRegex(@"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$")]
    private static partial Regex PlainNumberRegex();

    [GeneratedRegex(@"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))(ms|s)$")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))%$")]
    private static partial Regex PercentageRegex();

    [GeneratedRegex(@"^[A-Za-z_-][A-Za-z0-9_-]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"(translateX|translateY|scale)\(\s*([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*(px)?\s*\)")]
    private static partial Regex TransformFunctionRegex();

    private sealed class StyleBuilder
    {
        public string BackgroundColor { get; set; } = "#202226EE";
        public string TextColor { get; set; } = "#E6E7E9";
        public string BorderColor { get; set; } = "#454950";
        public string FontFamily { get; set; } = "Inter";
        public string TextAlign { get; set; } = "left";
        public double FontSize { get; set; } = 22;
        public double PaddingTop { get; set; } = 24;
        public double PaddingRight { get; set; } = 24;
        public double PaddingBottom { get; set; } = 24;
        public double PaddingLeft { get; set; } = 24;
        public double BorderWidth { get; set; } = 1;
        public double BorderRadius { get; set; } = 8;
        public double Width { get; set; } = 960;
        public double MinHeight { get; set; } = 150;
        public double Left { get; set; } = 32;
        public double Bottom { get; set; } = 32;
        public string? AnimationName { get; set; }
        public Declaration? AnimationNameDeclaration { get; set; }
        public TimeSpan? AnimationDuration { get; set; }
        public string? AnimationTimingFunction { get; set; }
        public double? AnimationIterationCount { get; set; }
        public bool AnimationIterationSpecified { get; set; }
        public bool AnimationInfinite { get; set; }
        public DialogueAnimation? Animation { get; set; }
        public Declaration? FirstAnimationOption { get; set; }

        public bool HasAnimationOptions =>
            AnimationDuration is not null || AnimationTimingFunction is not null || AnimationIterationSpecified;

        public DialogueStyle Build() => new(
            BackgroundColor,
            TextColor,
            BorderColor,
            FontFamily,
            TextAlign,
            FontSize,
            PaddingTop,
            PaddingRight,
            PaddingBottom,
            PaddingLeft,
            BorderWidth,
            BorderRadius,
            Width,
            MinHeight,
            Left,
            Bottom,
            Animation);
    }

    private sealed record Declaration(string Value, int Line, int Column, int ValueLine, int ValueColumn);

    private sealed record KeyframesRule(
        string Name,
        IReadOnlyList<KeyframeDeclaration> Frames,
        int Line,
        int Column);

    private sealed record KeyframeDeclaration(
        double Offset,
        IReadOnlyDictionary<string, Declaration> Declarations,
        int Line,
        int Column);
}
