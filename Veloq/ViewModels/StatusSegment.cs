namespace Veloq.ViewModels;

public sealed record StatusSegment(string Label, string Value, bool IsWarning = false)
{
    public bool HasLabel => Label.Length > 0;
}
