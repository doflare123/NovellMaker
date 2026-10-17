namespace NovellMaker.Core.Styling;

/// <summary>Reports an invalid or unsupported dialogue style.</summary>
public sealed class DialogueCssException : Exception
{
    public DialogueCssException(string message, int line, int column)
        : base($"{message} (строка {line}, столбец {column}).")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }

    public int Column { get; }
}
