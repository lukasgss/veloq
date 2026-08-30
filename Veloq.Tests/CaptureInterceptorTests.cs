using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Veloq.Data;
using Xunit;

namespace Veloq.Tests;

public sealed class CaptureInterceptorTests
{
    private sealed class Blog
    {
        public int Id { get; set; }
        public List<Post> Posts { get; } = [];
        public List<Tag> Tags { get; } = [];
    }

    private sealed class Post
    {
        public int Id { get; set; }
        public int BlogId { get; set; }
    }

    private sealed class Tag
    {
        public int Id { get; set; }
        public int BlogId { get; set; }
    }

    private sealed class BlogContext(DbContextOptions options) : DbContext(options)
    {
        public DbSet<Blog> Blogs => Set<Blog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>().HasMany(b => b.Posts).WithOne().HasForeignKey(p => p.BlogId);
            modelBuilder.Entity<Blog>().HasMany(b => b.Tags).WithOne().HasForeignKey(t => t.BlogId);
        }
    }

    [Fact]
    public async Task CountsFannedOutRowsFromTwoCollectionIncludes()
    {
        await using DbConnection connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        CaptureInterceptor interceptor = new();
        DbContextOptions options = new DbContextOptionsBuilder()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        const int blogs = 4;
        const int postsPer = 5;
        const int tagsPer = 3;

        await using (BlogContext seed = new(options))
        {
            await seed.Database.EnsureCreatedAsync();

            for (int b = 0; b < blogs; b++)
            {
                Blog blog = new();
                for (int p = 0; p < postsPer; p++)
                {
                    blog.Posts.Add(new Post());
                }

                for (int t = 0; t < tagsPer; t++)
                {
                    blog.Tags.Add(new Tag());
                }

                seed.Blogs.Add(blog);
            }

            await seed.SaveChangesAsync();
        }

        interceptor.Reset();

        await using BlogContext query = new(options);
        List<Blog> result = await query.Blogs
            .Include(b => b.Posts)
            .Include(b => b.Tags)
            .ToListAsync();

        CapturedCommand command = Assert.Single(interceptor.Commands);
        Assert.Equal(blogs, result.Count);
        Assert.Equal(blogs * postsPer * tagsPer, command.RowsFetched);
        Assert.True(command.RowsFetched > result.Count);

        MaterializedResult materialized = ResultMaterializer.Materialize(result);
        int displayCount = materialized.DisplayRowCount;
        int rootCount = materialized.RootCount;
        Assert.Equal(blogs, rootCount);
        Assert.Equal(blogs * postsPer * tagsPer, displayCount);

        int collectionIncludes = QueryDiagnostics.CountCollectionIncludes(
            "db.Blogs.Include(b => b.Posts).Include(b => b.Tags).ToListAsync()",
            typeof(Blog));
        Assert.Equal(2, collectionIncludes);

        Assert.False(QueryDiagnostics.IsCartesianExplosion(
            collectionIncludes, command.RowsFetched, displayCount, isSplitQuery: false),
            "using the expanded display count as denominator hides the explosion");
        Assert.True(QueryDiagnostics.IsCartesianExplosion(
            collectionIncludes, command.RowsFetched, rootCount, isSplitQuery: false));
    }

    [Fact]
    public async Task CountsRowsForPlainQuery()
    {
        await using DbConnection connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        CaptureInterceptor interceptor = new();
        DbContextOptions options = new DbContextOptionsBuilder()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using (BlogContext seed = new(options))
        {
            await seed.Database.EnsureCreatedAsync();
            for (int b = 0; b < 7; b++)
            {
                seed.Blogs.Add(new Blog());
            }

            await seed.SaveChangesAsync();
        }

        interceptor.Reset();

        await using BlogContext query = new(options);
        List<Blog> result = await query.Blogs.ToListAsync();

        CapturedCommand command = Assert.Single(interceptor.Commands);
        Assert.Equal(7, command.RowsFetched);
        Assert.Equal(7, result.Count);
    }
}
