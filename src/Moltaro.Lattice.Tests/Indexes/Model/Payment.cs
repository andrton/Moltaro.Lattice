namespace Moltaro.Lattice.Tests.Indexes.Model;

// Bound to the entity type "Payment" of ModelSchema.Composite, whose index ByKey is (Code, Amount, Payee):
// a Guid never null, a nullable Decimal and a String compared without case that is never null.
internal sealed class Payment
{
    public long Id { get; set; }

    public Guid Code { get; set; }

    public decimal? Amount { get; set; }

    public string Payee { get; set; } = "";
}
