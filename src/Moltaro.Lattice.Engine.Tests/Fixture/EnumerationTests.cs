using Moltaro.Lattice.Measurements;

namespace Moltaro.Lattice.Engine.Tests.Fixture;

// The sequence that ReferenceFixture.Generate returns is the same on every enumeration, not only on
// every call (brief of S05, "The fixture": Generate yields the records in the same order on every
// run): a caller may enumerate it twice, to digest it and to load it, and gets the same records both
// times. The sequence is enumerated exactly twice, which is the least that shows it; the scalar
// form is enough, since both forms come from the same enumeration.
public sealed class EnumerationTests
{
    [Fact]
    public void Every_enumeration_of_one_generated_sequence_yields_the_same_records()
    {
        var sequence = ReferenceFixture.Generate(FixtureForms.Compile(FixtureForms.Scalar));

        var first = sequence.ToList();
        var second = sequence.ToList();

        Assert.Equal(first.Count, second.Count);

        for (var index = 0; index < first.Count; index++)
        {
            Assert.True(RecordValues.Equal(first[index], second[index]), $"The record at {index} differs between the two enumerations.");
        }

        Assert.Equal(ReferenceFixture.Digest(first), ReferenceFixture.Digest(second));
    }
}
