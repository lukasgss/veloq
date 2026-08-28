using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Veloq.Data;

internal static class ResultMaterializer
{
    private const int MaxDisplayRows = 500;

    public static (List<string> Columns, List<string[]> Rows, int Count) Materialize(object? value)
    {
        if (value is null || IsScalarValue(value.GetType()))
        {
            return (["Value"], [[Format(value)]], 1);
        }

        List<object?> items = value is IEnumerable enumerable
            ? enumerable.Cast<object?>().ToList()
            : [value];

        int total = items.Count;
        if (total == 0)
        {
            return ([], [], 0);
        }

        Type? elementType = items.First(item => item is not null)?.GetType();
        PropertyInfo[]? properties = elementType is null || IsScalarValue(elementType)
            ? null
            : elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .ToArray();

        if (properties is null)
        {
            return (["Value"], items.Take(MaxDisplayRows).Select(item => new[] { Format(item) }).ToList(), total);
        }

        List<string> columns = properties.Select(property => property.Name).ToList();
        List<string[]> rows = items.Take(MaxDisplayRows)
            .Select(item => properties.Select(property => Format(property.GetValue(item))).ToArray())
            .ToList();

        return (columns, rows, total);
    }

    private static bool IsScalarValue(Type type) =>
        type.IsPrimitive || type.IsEnum ||
        type == typeof(string) || type == typeof(decimal) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
        type == typeof(Guid) || type == typeof(TimeSpan);

    private static string Format(object? value) => value switch
    {
        null => "null",
        DateTime { Kind: DateTimeKind.Utc } date => date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:sszzz"),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss"),
        DateTimeOffset offset => offset.ToLocalTime().ToString("yyyy-MM-dd HH:mm:sszzz"),
        _ => value.ToString() ?? "null",
    };
}
