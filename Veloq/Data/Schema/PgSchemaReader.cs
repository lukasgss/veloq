using Npgsql;

namespace Veloq.Data.Schema;

public static class PgSchemaReader
{
    private const string SystemSchemas = "'pg_catalog','information_schema'";

    public static async Task<DatabaseModel> ReadAsync(string connectionString)
    {
        DatabaseModel model = new();
        Dictionary<string, TableModel> tablesByKey = [];

        await using NpgsqlConnection conn = new(connectionString);
        await conn.OpenAsync();

        const string columnsSql = $"""
            SELECT c.table_schema, c.table_name, c.column_name, c.udt_name, c.is_nullable
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE t.table_type = 'BASE TABLE'
              AND c.table_schema NOT IN ({SystemSchemas})
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """;

        await using (NpgsqlCommand cmd = new(columnsSql, conn))
        await using (NpgsqlDataReader r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                string schema = r.GetString(0);
                string name = r.GetString(1);
                string key = $"{schema}.{name}";
                if (!tablesByKey.TryGetValue(key, out TableModel? table))
                {
                    table = new TableModel { Schema = schema, Name = name };
                    tablesByKey[key] = table;
                    model.Tables.Add(table);
                }

                table.Columns.Add(new ColumnModel
                {
                    Name = r.GetString(2),
                    UdtName = r.GetString(3),
                    IsNullable = r.GetString(4) == "YES",
                });
            }
        }

        const string primaryKeysSql = $"""
            SELECT tc.table_schema, tc.table_name, kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
             AND tc.table_schema = kcu.table_schema
            WHERE tc.constraint_type = 'PRIMARY KEY'
              AND tc.table_schema NOT IN ({SystemSchemas})
            """;

        await using (NpgsqlCommand cmd = new(primaryKeysSql, conn))
        await using (NpgsqlDataReader r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                string key = $"{r.GetString(0)}.{r.GetString(1)}";
                string col = r.GetString(2);

                if (tablesByKey.TryGetValue(key, out TableModel? table))
                {
                    ColumnModel? c = table.Columns.Find(x => x.Name == col);
                    c?.IsPrimaryKey = true;
                }
            }
        }

        const string foreignKeySql = $"""
            SELECT tc.constraint_name, tc.table_schema, tc.table_name, kcu.column_name,
                   ccu.table_schema, ccu.table_name, ccu.column_name,
                   COUNT(*) OVER (PARTITION BY tc.constraint_name) AS col_count
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
              ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema NOT IN ({SystemSchemas})
            """;

        await using (NpgsqlCommand cmd = new(foreignKeySql, conn))
        await using (NpgsqlDataReader r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                if (IsCompositeForeignKey(r))
                {
                    continue;
                }

                model.ForeignKeys.Add(new ForeignKeyModel
                {
                    Name = r.GetString(0),
                    Schema = r.GetString(1),
                    Table = r.GetString(2),
                    Column = r.GetString(3),
                    RefSchema = r.GetString(4),
                    RefTable = r.GetString(5),
                    RefColumn = r.GetString(6),
                });
            }
        }

        return model;
    }

    private const int ColumnCountOrdinal = 7;

    private static bool IsCompositeForeignKey(NpgsqlDataReader reader) => reader.GetInt64(ColumnCountOrdinal) != 1;
}
