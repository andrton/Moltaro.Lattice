namespace Moltaro.Lattice.Tests.Indexes.Model.Composites;

// Bound to the entity type "Payment" of CompositeSchema, whose index ByKey is (Code, Payee, Amount):
// a Guid never null, a String compared without case that is never null, and a nullable Decimal.
internal sealed class Payment
{
    public long Id { get; set; }

    public Guid Code { get; set; }

    public decimal? Amount { get; set; }

    public string Payee { get; set; } = "";
}
