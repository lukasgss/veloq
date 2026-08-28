namespace Veloq.Data;

/// <summary>Symbols available inside the user's LINQ expression.</summary>
public sealed class ScriptGlobals
{
    public ECommerceDbContext db = null!;
    public string country = string.Empty;
}
