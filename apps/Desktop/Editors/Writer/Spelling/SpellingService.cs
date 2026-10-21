namespace Nexolume.Desktop.Editors.Writer.Spelling;

public sealed record SpellingIssue(int Start, int Length, string Word, IReadOnlyList<string> Suggestions);
public sealed record SpellingResult(bool Available, IReadOnlyList<SpellingIssue> Issues, string? Message = null);

public sealed class SpellingService
{
    // Each call owns and releases all its COM objects on the same background thread.
    public Task<SpellingResult> CheckAsync(string text, string language) => Task.Run(() => WindowsSpelling.Check(text, language));
}
