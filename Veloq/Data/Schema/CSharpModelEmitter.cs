using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Veloq.Data.Schema;

public static class CSharpModelEmitter
{
    public const string Namespace = "Veloq.Generated";
    public const string ContextName = "GeneratedDbContext";
    public const string HostName = "ScriptHost";

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    ];

    public static string Emit(DatabaseModel model)
    {
        InferConventionForeignKeys(model);
        AssignNames(model);

        StringBuilder sb = new();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Microsoft.EntityFrameworkCore;");
        sb.AppendLine();
        sb.AppendLine($"namespace {Namespace};");
        sb.AppendLine();

        EmitEntities(sb, model);
        EmitContext(sb, model);
        EmitHost(sb);

        return sb.ToString();
    }

    private static void EmitEntities(StringBuilder sb, DatabaseModel model)
    {
        ILookup<string, ForeignKeyModel> refNavs = model.ForeignKeys.ToLookup(f => $"{f.Schema}.{f.Table}");
        ILookup<string, ForeignKeyModel> collNavs = model.ForeignKeys.ToLookup(f => $"{f.RefSchema}.{f.RefTable}");
        Dictionary<string, TableModel> byKey = model.Tables.ToDictionary(t => t.Key);

        foreach (TableModel table in model.Tables)
        {
            sb.AppendLine($"public sealed class {table.ClrName}");
            sb.AppendLine("{");

            foreach (ColumnModel col in table.Columns)
            {
                (string csType, bool _) = PgTypeMap.Map(col.UdtName);
                string nullable = col.IsNullable ? "?" : string.Empty;
                string init = csType == "string" && !col.IsNullable ? " = string.Empty;" : string.Empty;
                sb.AppendLine($"\tpublic {csType}{nullable} {col.ClrName} {{ get; set; }}{init}");
            }

            foreach (ForeignKeyModel fk in refNavs[table.Key])
            {
                if (byKey.TryGetValue($"{fk.RefSchema}.{fk.RefTable}", out TableModel? principal))
                {
                    sb.AppendLine($"\tpublic {principal.ClrName}? {fk.ReferenceNavName} {{ get; set; }}");
                }
            }

            foreach (ForeignKeyModel fk in collNavs[table.Key])
            {
                if (byKey.TryGetValue($"{fk.Schema}.{fk.Table}", out TableModel? dependent))
                {
                    sb.AppendLine($"\tpublic List<{dependent.ClrName}> {fk.CollectionNavName} {{ get; }} = new();");
                }
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }
    }

    private static void EmitContext(StringBuilder sb, DatabaseModel model)
    {
        Dictionary<string, TableModel> byKey = model.Tables.ToDictionary(t => t.Key);

        sb.AppendLine($"public sealed class {ContextName} : DbContext");
        sb.AppendLine("{");
        sb.AppendLine($"\tpublic {ContextName}(DbContextOptions options) : base(options) {{ }}");
        sb.AppendLine();

        foreach (TableModel t in model.Tables)
        {
            sb.AppendLine($"\tpublic DbSet<{t.ClrName}> {t.ClrName} => Set<{t.ClrName}>();");
        }

        sb.AppendLine();
        sb.AppendLine("\tprotected override void OnModelCreating(ModelBuilder b)");
        sb.AppendLine("\t{");

        foreach (TableModel t in model.Tables)
        {
            string e = $"b.Entity<{t.ClrName}>()";
            sb.AppendLine($"\t\t{e}.ToTable(\"{t.Name}\", \"{t.Schema}\");");

            List<ColumnModel> primaryKey = t.Columns.Where(c => c.IsPrimaryKey).ToList();
            if (primaryKey.Count > 0)
            {
                sb.AppendLine($"\t\t{e}.HasKey({KeySelector(primaryKey)});");
            }
            else
            {
                sb.AppendLine($"\t\t{e}.HasNoKey();");
            }

            foreach (ColumnModel column in t.Columns)
            {
                sb.AppendLine($"\t\t{e}.Property(x => x.{column.ClrName}).HasColumnName(\"{column.Name}\");");
            }
        }

        foreach (ForeignKeyModel fk in model.ForeignKeys)
        {
            if (!byKey.TryGetValue($"{fk.Schema}.{fk.Table}", out TableModel? dep))
            {
                continue;
            }

            if (!byKey.TryGetValue($"{fk.RefSchema}.{fk.RefTable}", out TableModel? principal))
            {
                continue;
            }

            ColumnModel depCol = dep.Columns.First(c => c.Name == fk.Column);
            ColumnModel refCol = principal.Columns.First(c => c.Name == fk.RefColumn);

            sb.AppendLine(
                $"\t\tb.Entity<{dep.ClrName}>()" +
                $".HasOne(x => x.{fk.ReferenceNavName})" +
                $".WithMany(x => x.{fk.CollectionNavName})" +
                $".HasPrincipalKey(x => x.{refCol.ClrName})" +
                $".HasForeignKey(x => x.{depCol.ClrName});");
        }

        sb.AppendLine("\t}");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string KeySelector(List<ColumnModel> pk)
    {
        return pk.Count == 1
             ? $"x => x.{pk[0].ClrName}"
             : $"x => new {{ {string.Join(", ", pk.Select(c => "x." + c.ClrName))} }}";
    }

    private static void EmitHost(StringBuilder sb)
    {
        sb.AppendLine($"public sealed class {HostName}");
        sb.AppendLine("{");
        sb.AppendLine($"\tpublic {ContextName} db = null!;");
        sb.AppendLine("}");
    }

    private static void InferConventionForeignKeys(DatabaseModel model)
    {
        HashSet<string> mappedColumns = model.ForeignKeys
            .Select(foreignKey => $"{foreignKey.Schema}.{foreignKey.Table}.{foreignKey.Column}")
            .ToHashSet();

        foreach (TableModel dependent in model.Tables)
        {
            foreach (ColumnModel column in dependent.Columns)
            {
                string columnKey = $"{dependent.Schema}.{dependent.Name}.{column.Name}";
                if (mappedColumns.Contains(columnKey))
                {
                    continue;
                }

                string targetName = Pascal(StripId(column.Name));
                if (targetName == Pascal(column.Name))
                {
                    continue;
                }

                IReadOnlyList<TableModel> candidates = model.Tables
                    .Where(table => Pascal(table.Name) == targetName)
                    .Where(table => table.Columns.Count(candidate => candidate.IsPrimaryKey) == 1)
                    .ToList();

                IReadOnlyList<TableModel> sameSchema = candidates
                    .Where(table => table.Schema == dependent.Schema)
                    .ToList();

                TableModel? principal = null;

                if (sameSchema.Count == 1)
                {
                    principal = sameSchema[0];
                }
                else if (sameSchema.Count == 0 && candidates.Count == 1)
                {
                    principal = candidates[0];
                }

                if (principal is null)
                {
                    continue;
                }

                ColumnModel principalKey = principal.Columns.Single(candidate => candidate.IsPrimaryKey);
                model.ForeignKeys.Add(new ForeignKeyModel
                {
                    Name = $"inferred_{dependent.Name}_{column.Name}",
                    Schema = dependent.Schema,
                    Table = dependent.Name,
                    Column = column.Name,
                    RefSchema = principal.Schema,
                    RefTable = principal.Name,
                    RefColumn = principalKey.Name,
                });
                mappedColumns.Add(columnKey);
            }
        }
    }

    private static void AssignNames(DatabaseModel model)
    {
        HashSet<string> usedClass = [];

        foreach (TableModel table in model.Tables)
        {
            table.ClrName = Unique(usedClass, Pascal(table.Name));
        }

        foreach (TableModel tableModel in model.Tables)
        {
            HashSet<string> used = [tableModel.ClrName];

            foreach (ColumnModel column in tableModel.Columns)
            {
                column.ClrName = Unique(used, Pascal(column.Name));
            }

            AssignNavNames(model, tableModel, used);
        }
    }

    private static void AssignNavNames(DatabaseModel model, TableModel table, HashSet<string> used)
    {
        Dictionary<string, TableModel> byKey = model.Tables.ToDictionary(x => x.Key);

        foreach (ForeignKeyModel fk in model.ForeignKeys.Where(f => f.Schema == table.Schema && f.Table == table.Name))
        {
            if (byKey.ContainsKey($"{fk.RefSchema}.{fk.RefTable}"))
            {
                fk.ReferenceNavName = Unique(used, Pascal(StripId(fk.Column)));
            }
        }

        foreach (ForeignKeyModel fk in model.ForeignKeys.Where(f => f.RefSchema == table.Schema && f.RefTable == table.Name))
        {
            if (byKey.TryGetValue($"{fk.Schema}.{fk.Table}", out TableModel? dependent))
            {
                fk.CollectionNavName = Unique(used, dependent.ClrName + "Items");
            }
        }
    }

    private static string StripId(string col)
    {
        if (col.EndsWith("_id", System.StringComparison.OrdinalIgnoreCase))
        {
            return col[..^3];
        }

        if (col.EndsWith("id", System.StringComparison.OrdinalIgnoreCase) && col.Length > 2)
        {
            return col[..^2];
        }

        return col;
    }

    private static string Unique(HashSet<string> used, string name)
    {
        string candidate = name;
        int i = 1;

        while (!used.Add(candidate))
        {
            i++;
            candidate = name + i;
        }

        return candidate;
    }

    private static string Pascal(string raw)
    {
        string[] parts = raw.Split(['_', ' ', '-', '.'], System.StringSplitOptions.RemoveEmptyEntries);

        StringBuilder sb = new();

        foreach (string p in parts)
        {
            sb.Append(char.ToUpperInvariant(p[0]));

            if (p.Length > 1)
            {
                sb.Append(p[1..]);
            }
        }

        string result = sb.Length == 0 ? "Col" : sb.ToString();
        if (!char.IsLetter(result[0]) && result[0] != '_')
        {
            result = "_" + result;
        }

        if (Keywords.Contains(result))
        {
            result += "_";
        }

        return result;
    }
}
