using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace Veloq.Data;

internal sealed record MaterializedResult(
    List<string> Columns,
    List<string[]> Rows,
    int DisplayRowCount,
    int RootCount,
    Type? RootType = null);

internal static class ResultMaterializer
{
    private const int MaxDisplayRows = 500;
    private const int MaxNavigationDepth = 3;
    private const int MaxCollectionItems = 20;

    public static MaterializedResult Materialize(object? value)
    {
        if (value is null || IsScalarValue(value.GetType()))
        {
            return new MaterializedResult(["Value"], [[Format(value)]], DisplayRowCount: 1, RootCount: 1, RootType: value?.GetType());
        }

        List<object?> items = value is IEnumerable enumerable
            ? enumerable.Cast<object?>().ToList()
            : [value];

        int total = items.Count;
        if (total == 0)
        {
            return new MaterializedResult([], [], DisplayRowCount: 0, RootCount: 0);
        }

        Type? elementType = items.First(item => item is not null)?.GetType();
        PropertyInfo[] properties = elementType is null || IsScalarValue(elementType)
            ? []
            : elementType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Where(property => ShouldDisplayTopLevelProperty(property, items))
                .ToArray();

        if (properties.Length == 0)
        {
            return new MaterializedResult(
                ["Value"],
                items.Take(MaxDisplayRows).Select(item => new[] { Format(item) }).ToList(),
                DisplayRowCount: total,
                RootCount: total,
                RootType: elementType);
        }

        List<DisplayColumn> displayColumns = BuildDisplayColumns(properties, items);

        List<string> columns = displayColumns.Select(column => column.Name).ToList();
        List<PropertyInfo> collectionProperties = displayColumns
            .Where(column => column.IsCollection)
            .Select(column => column.Property)
            .Distinct()
            .ToList();
        List<string[]> rows = [];
        int expandedTotal = 0;

        foreach (object? item in items)
        {
            List<CollectionExpansion> expansions = GetCollectionExpansions(item, collectionProperties);
            expandedTotal = AddWithoutOverflow(expandedTotal, CountExpandedRows(expansions));

            int remainingRows = MaxDisplayRows - rows.Count;
            if (remainingRows == 0)
            {
                continue;
            }

            foreach (Dictionary<PropertyInfo, object?> context in ExpandCollections(expansions, remainingRows))
            {
                HashSet<object> path = new(ReferenceEqualityComparer.Instance);
                if (item is not null && !item.GetType().IsValueType)
                {
                    path.Add(item);
                }

                rows.Add(displayColumns
                    .Select(column => Format(column.GetValue(item, context), path, depth: 0))
                    .ToArray());
            }
        }

        return new MaterializedResult(columns, rows, DisplayRowCount: expandedTotal, RootCount: total, RootType: elementType);
    }

    private static bool IsScalarValue(Type type) =>
        type.IsPrimitive || type.IsEnum ||
        type == typeof(string) || type == typeof(decimal) ||
        type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
        type == typeof(Guid) || type == typeof(TimeSpan);

    private static string Format(object? value)
    {
        return Format(value, new HashSet<object>(ReferenceEqualityComparer.Instance), depth: 0);
    }

    private static string Format(object? value, HashSet<object> path, int depth)
    {
        if (value is null)
        {
            return "null";
        }

        Type type = value.GetType();
        if (IsScalarValue(type))
        {
            return FormatScalar(value);
        }

        bool tracksReference = !type.IsValueType;
        if (tracksReference && !path.Add(value))
        {
            return "↩";
        }

        try
        {
            if (depth >= MaxNavigationDepth)
            {
                return "…";
            }

            if (value is IEnumerable enumerable)
            {
                List<string> values = enumerable.Cast<object?>()
                    .Take(MaxCollectionItems + 1)
                    .Select(item => Format(item, path, depth + 1))
                    .ToList();

                bool truncated = values.Count > MaxCollectionItems;
                if (truncated)
                {
                    values.RemoveAt(MaxCollectionItems);
                    values.Add("…");
                }

                return $"[{string.Join(", ", values)}]";
            }

            IEnumerable<PropertyInfo> properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetIndexParameters().Length == 0)
                .Where(property => IsScalarProperty(property.PropertyType));

            return "{ " + string.Join(", ", properties.Select(property =>
                $"{property.Name} = {Format(property.GetValue(value), path, depth + 1)}")) + " }";
        }
        finally
        {
            if (tracksReference)
            {
                path.Remove(value);
            }
        }
    }

    private static bool IsScalarProperty(Type type) => IsScalarValue(Nullable.GetUnderlyingType(type) ?? type);

    private static bool ShouldDisplayTopLevelProperty(PropertyInfo property, IEnumerable<object?> items)
    {
        if (IsScalarProperty(property.PropertyType))
        {
            return true;
        }

        List<object?> values = GetPropertyValues(property, items);
        if (typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
        {
            return values.OfType<IEnumerable>().Any(value => value.Cast<object?>().Any());
        }

        return values.Any(value => value is not null);
    }

    private static List<DisplayColumn> BuildDisplayColumns(
        IEnumerable<PropertyInfo> properties,
        IReadOnlyCollection<object?> items)
    {
        List<DisplayColumn> columns = [];

        foreach (PropertyInfo property in properties)
        {
            if (IsScalarProperty(property.PropertyType))
            {
                columns.Add(new DisplayColumn(property.Name, property, null, IsCollection: false));
                continue;
            }

            if (typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
            {
                object? collectionItem = GetPropertyValues(property, items)
                    .OfType<IEnumerable>()
                    .SelectMany(collection => collection.Cast<object?>())
                    .FirstOrDefault(item => item is not null);
                if (collectionItem is null || IsScalarValue(collectionItem.GetType()))
                {
                    columns.Add(new DisplayColumn(property.Name, property, null, IsCollection: true));
                    continue;
                }

                PropertyInfo[] itemProperties = collectionItem.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(itemProperty => itemProperty.GetIndexParameters().Length == 0 &&
                                           IsScalarProperty(itemProperty.PropertyType))
                    .ToArray();

                columns.AddRange(itemProperties.Select(itemProperty =>
                    new DisplayColumn(
                        $"{property.Name}.{itemProperty.Name}",
                        property,
                        itemProperty,
                        IsCollection: true)));
                continue;
            }

            object? reference = GetPropertyValues(property, items).FirstOrDefault(value => value is not null);
            if (reference is null)
            {
                continue;
            }

            PropertyInfo[] scalarProperties = reference.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(nested => nested.GetIndexParameters().Length == 0)
                .Where(nested => IsScalarProperty(nested.PropertyType))
                .ToArray();

            columns.AddRange(scalarProperties.Select(nested =>
                new DisplayColumn(
                    $"{property.Name}.{nested.Name}",
                    property,
                    nested,
                    IsCollection: false)));
        }

        return columns;
    }

    private static List<object?> GetPropertyValues(PropertyInfo property, IEnumerable<object?> items)
    {
        return items
            .Where(item => item is not null)
            .Select(property.GetValue)
            .ToList();
    }

    private static List<CollectionExpansion> GetCollectionExpansions(
        object? item,
        IEnumerable<PropertyInfo> collectionProperties)
    {
        List<CollectionExpansion> expansions = [];
        foreach (PropertyInfo property in collectionProperties)
        {
            object? value = item is null ? null : property.GetValue(item);
            List<object?> collectionItems = value is IEnumerable collection
                ? collection.Cast<object?>().ToList()
                : [];
            if (collectionItems.Count == 0)
            {
                collectionItems.Add(null);
            }

            expansions.Add(new CollectionExpansion(property, collectionItems));
        }

        return expansions;
    }

    private static int CountExpandedRows(IEnumerable<CollectionExpansion> expansions)
    {
        int count = 1;
        foreach (CollectionExpansion expansion in expansions)
        {
            if (count > int.MaxValue / expansion.Items.Count)
            {
                return int.MaxValue;
            }

            count *= expansion.Items.Count;
        }

        return count;
    }

    private static int AddWithoutOverflow(int left, int right) =>
        left > int.MaxValue - right ? int.MaxValue : left + right;

    private static List<Dictionary<PropertyInfo, object?>> ExpandCollections(
        IEnumerable<CollectionExpansion> expansions,
        int limit)
    {
        List<Dictionary<PropertyInfo, object?>> contexts = [new()];

        foreach (CollectionExpansion expansion in expansions)
        {
            List<Dictionary<PropertyInfo, object?>> next = [];

            foreach (Dictionary<PropertyInfo, object?> context in contexts)
            {
                foreach (object? collectionItem in expansion.Items)
                {
                    Dictionary<PropertyInfo, object?> expanded = new(context)
                    {
                        [expansion.Property] = collectionItem,
                    };

                    next.Add(expanded);
                    if (next.Count == limit)
                    {
                        break;
                    }
                }

                if (next.Count == limit)
                {
                    break;
                }
            }

            contexts = next;
        }

        return contexts;
    }

    private sealed record CollectionExpansion(PropertyInfo Property, List<object?> Items);

    private sealed record DisplayColumn(
        string Name,
        PropertyInfo Property,
        PropertyInfo? NestedProperty,
        bool IsCollection)
    {
        public object? GetValue(object? item, IReadOnlyDictionary<PropertyInfo, object?> expandedCollections)
        {
            if (item is null)
            {
                return null;
            }

            object? value;
            if (IsCollection)
            {
                expandedCollections.TryGetValue(Property, out value);
            }
            else
            {
                value = Property.GetValue(item);
            }

            if (value is null || NestedProperty is null)
            {
                return value;
            }

            return NestedProperty.GetValue(value);
        }
    }

    private static string FormatScalar(object value) => value switch
    {
        DateTime { Kind: DateTimeKind.Utc } date => date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:sszzz"),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss"),
        DateTimeOffset offset => offset.ToLocalTime().ToString("yyyy-MM-dd HH:mm:sszzz"),
        _ => value.ToString() ?? "null",
    };
}
