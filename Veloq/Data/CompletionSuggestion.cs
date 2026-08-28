namespace Veloq.Data;

public sealed record CompletionSuggestion(
    string Text,
    string Description,
    string Detail,
    string Kind,
    int Priority);
