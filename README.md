# Veloq — LINQ Playground

Desktop (Avalonia) app to write EF Core LINQ against **your own PostgreSQL** database
and inspect the generated SQL, round-trip count (N+1), and the **real query planner**
output (`EXPLAIN ANALYZE`).

## Run

```bash
dotnet run --project Veloq.Desktop
```

1. Enter your PostgreSQL connection details on the connect screen.
2. (Optional) tick *seed sample Customers/Orders schema* to load a demo dataset.
3. Write a LINQ expression that returns an `IEnumerable`, using the in-scope symbols:
   - `db` — an `ECommerceDbContext`
   - `country` — the toolbar `country` string
4. **Run** to see Results / SQL / Query Plan and the round-trip count.

The default expression is a deliberate **N+1**; rewrite it into a single query and
watch the round-trip count drop from N+1 to 1.

## Layout

```
Veloq/            shared UI + query engine
  Data/           EF Core context, entities, Roslyn QueryRunner, EXPLAIN
  ViewModels/     MVVM
  Views/          connect screen + editor/output workspace
Veloq.Desktop/    desktop entry point
```
