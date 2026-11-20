using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Basics;

// An entity class does not have to be open for writing. The property of a field needs a public get
// accessor and a set accessor of any accessibility: public, internal, protected, private or init.
// The database sets the stored values through those accessors when it builds an object, so a class
// can keep its state behind its constructor and its methods and still be read and written.
internal static class ClosedPropertiesSample
{
    private static readonly string SchemaJson = $$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Order", "ClrType": "{{typeof(Order).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Customer", "Type": "String" },
                { "Id": 3, "Name": "Total", "Type": "Decimal" },
                { "Id": 4, "Name": "Status", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    public static async Task RunAsync()
    {
        Console.WriteLine("== An entity class closed for writing ==");

        await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = LatticeSchema.Compile(SchemaJson),
        });
        var orders = database.Set<Order>();

        // The application makes an order through its constructor; the database reads its values
        // through the public get accessors.
        await orders.InsertAsync(new Order(1, "Ada", 120.50m));

        // The copy that is read back is built by the database, through the closed set accessors.
        var order = orders.Get(1L)!;
        Console.WriteLine($"Read: {order}");

        // The application changes the order only through its methods, and writes it back.
        order.Ship();
        await orders.ReplaceAsync(order);
        Console.WriteLine($"After shipping: {orders.Get(1L)}");
        Console.WriteLine();
    }

    // Nothing of an order can be set from outside but through its constructor and Ship. The
    // database builds an object with the public parameterless constructor and sets each property
    // through its own set accessor.
    private sealed class Order
    {
        public Order()
        {
        }

        public Order(long id, string customer, decimal total)
        {
            Id = id;
            Customer = customer;
            Total = total;
            Status = "Open";
        }

        public long Id { get; init; }

        public string Customer { get; private set; } = "";

        public decimal Total { get; private set; }

        public string Status { get; private set; } = "";

        public void Ship() => Status = "Shipped";

        public override string ToString() => $"#{Id} {Customer}, {Total}, {Status}";
    }
}
