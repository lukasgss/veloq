# Veloq — LINQ Playground

Desktop (Avalonia) app to write EF Core LINQ against **your own PostgreSQL** database
and inspect the generated SQL, round-trip count (N+1), and the **real query planner**
output (`EXPLAIN ANALYZE`).

## Run

```bash
dotnet run --project Veloq.Desktop
```

1. Add your PostgreSQL connection details. Connections are restored from local app data
   on later launches.
2. Write a LINQ expression that returns an `IEnumerable`. `db` is a `DbContext`
   **generated at runtime from your live schema** (one `DbSet`/entity per table,
   with navigation properties for single-column FKs and unambiguous `<Table>Id` columns).

   Autocomplete opens after `.` for .NET/EF methods, fetched `DbSet` tables, columns,
   and navigation properties. Press `Ctrl+Space` to open it manually.

   On connect, Veloq introspects the database (`information_schema` / `pg_catalog`),
   emits a matching EF Core model, and compiles it in-memory with Roslyn. Run executes
   your LINQ against that model. Table `orders` → `db.Orders`, column `full_name`
   → `.FullName`, FK `books.author_id` → `book.Author` / `author.BooksItems`.
3. **Run** to see Results / SQL / Query Plan and the round-trip count.

## Layout

```
Veloq/            shared UI + query engine
  Data/           Roslyn QueryRunner, capture interceptor, EXPLAIN
  Data/Schema/    PostgreSQL introspection → C# emitter → in-memory model compiler
  ViewModels/     MVVM
  Views/          connect screen + editor/output workspace
Veloq.Desktop/    desktop entry point
```
