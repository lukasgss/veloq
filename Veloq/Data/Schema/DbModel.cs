using System.Collections.Generic;

namespace Veloq.Data.Schema;

public sealed class DatabaseModel
{
    public List<TableModel> Tables { get; } = [];
    public List<ForeignKeyModel> ForeignKeys { get; } = [];
}

public sealed class TableModel
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public List<ColumnModel> Columns { get; } = [];

    public string ClrName { get; set; } = string.Empty;

    public string Key => $"{Schema}.{Name}";
}

public sealed class ColumnModel
{
    public required string Name { get; init; }
    public required string UdtName { get; init; }
    public required bool IsNullable { get; init; }
    public bool IsPrimaryKey { get; set; }

    public string ClrName { get; set; } = string.Empty;
}

public sealed class ForeignKeyModel
{
    public required string Name { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required string Column { get; init; }
    public required string RefSchema { get; init; }
    public required string RefTable { get; init; }
    public required string RefColumn { get; init; }

    public string ReferenceNavName { get; set; } = string.Empty;
    public string CollectionNavName { get; set; } = string.Empty;
}
