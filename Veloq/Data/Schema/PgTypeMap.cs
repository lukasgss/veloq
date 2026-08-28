namespace Veloq.Data.Schema;

public static class PgTypeMap
{
    public static (string CsType, bool IsValueType) Map(string udtName)
    {
        return udtName switch
        {
            "bool" => ("bool", true),
            "int2" => ("short", true),
            "int4" or "serial4" => ("int", true),
            "int8" or "serial8" => ("long", true),
            "float4" => ("float", true),
            "float8" => ("double", true),
            "numeric" or "money" => ("decimal", true),
            "uuid" => ("System.Guid", true),
            "date" or "timestamp" => ("System.DateTime", true),
            "timestamptz" => ("System.DateTime", true),
            "timetz" or "time" => ("System.TimeSpan", true),
            "interval" => ("System.TimeSpan", true),
            "bytea" => ("byte[]", false),
            _ => ("string", false),
        };
    }
}
