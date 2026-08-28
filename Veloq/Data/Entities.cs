using System;
using System.Collections.Generic;

namespace Veloq.Data;

public sealed class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public List<Order> Orders { get; set; } = new();
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public decimal Total { get; set; }
    public DateTime PlacedAt { get; set; }
}

public sealed class CustomerTotal
{
    public int CustomerId { get; set; }
    public decimal Total { get; set; }
}
