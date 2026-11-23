namespace Moltaro.Lattice.Tests.Indexes.Model;

// Bound to the entity type "Resident" of ModelSchema.OwnedPath: Home, a nullable owned value of the owned
// type Residence, whose City is indexed through the path Home.City.
internal sealed class Resident
{
    public long Id { get; set; }

    public Residence? Home { get; set; }
}
