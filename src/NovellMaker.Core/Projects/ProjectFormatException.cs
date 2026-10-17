namespace NovellMaker.Core.Projects;

/// <summary>
/// Indicates that a project file cannot be interpreted without risking data loss.
/// </summary>
public sealed class ProjectFormatException : Exception
{
    public ProjectFormatException(string message)
        : base(message)
    {
    }

    public ProjectFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
