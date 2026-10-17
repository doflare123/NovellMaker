namespace NovellMaker.Editor.Services.Git;

public sealed record GitStatusSnapshot(
    GitAvailability Availability,
    string? RepositoryRoot,
    IReadOnlyList<GitFileChange> Changes,
    string Message);
