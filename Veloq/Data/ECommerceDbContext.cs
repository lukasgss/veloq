using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace Veloq.Data;

public sealed class ECommerceDbContext : DbContext
{
    public ECommerceDbContext(DbContextOptions<ECommerceDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Customer>().HasIndex(c => c.Country);
        b.Entity<Order>().HasIndex(o => o.CustomerId);
        b.Entity<Order>().Property(o => o.Total).HasColumnType("numeric(12,2)");
    }

    /// <summary>Create schema and seed a realistic dataset if empty.</summary>
    public void EnsureSeeded(int customers = 5000, int maxOrdersPerCustomer = 8)
    {
        Database.EnsureCreated();
        if (Customers.Any()) return;

        var countries = new[] { "US", "DE", "FR", "GB", "BR", "JP", "CA", "AU" };
        var rng = new Random(42);
        var custList = new Customer[customers];
        for (var i = 0; i < customers; i++)
        {
            custList[i] = new Customer
            {
                Name = $"Customer {i + 1}",
                Country = countries[rng.Next(countries.Length)],
            };
        }
        Customers.AddRange(custList);
        SaveChanges();

        foreach (var c in custList)
        {
            var n = rng.Next(1, maxOrdersPerCustomer + 1);
            for (var j = 0; j < n; j++)
            {
                Orders.Add(new Order
                {
                    CustomerId = c.Id,
                    Total = Math.Round((decimal)(rng.NextDouble() * 500 + 5), 2),
                    PlacedAt = DateTime.UtcNow.AddDays(-rng.Next(0, 365)),
                });
            }
        }
        SaveChanges();
    }
}
