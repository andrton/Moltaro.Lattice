using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Fields;

// Which member serves which field (brief of S05, "Which member serves which field"). SetInteger and
// GetInteger serve Int8 to Int64 and an Enum of a signed underlying type; SetUnsigned and
// GetUnsigned UInt8 to UInt64 and an Enum of an unsigned underlying type; SetNull and IsNull every
// field; GetLength a String and a Binary field; every other member the one field type of its name.
// A member called for a field of another type, or for a field of another entity type, is
// ArgumentException. Every member is tried with every field of EveryType, whose entity type Twin
// has the same fields as Item. Each refused call of the writer is made on a writer of its own,
// which is abandoned afterwards.
public sealed class WrongFieldTests
{
    [Fact]
    public void A_Set_member_called_for_a_field_of_another_type_is_ArgumentException()
    {
        using var item = EveryType.Open();

        foreach (var (member, set, serves) in WriterMembers.Setters)
        {
            foreach (var field in item.EntityType.Fields.Where(field => !serves(field.Declaration)))
            {
                var writer = item.Writer();
                var exception = WriterAssert.Catch(ref writer, set(field));

                Assert.True(exception?.GetType() == typeof(ArgumentException), $"{member} of {field.Name} threw {exception?.GetType().Name ?? "nothing"}.");
                writer.Abandon();
            }
        }
    }

    [Fact]
    public void Every_Set_member_and_SetNull_called_for_a_field_of_another_entity_type_are_ArgumentException()
    {
        using var item = EveryType.Open();
        Assert.True(item.Schema.TryGetEntityType("Twin", out var twin));

        foreach (var field in twin.Fields)
        {
            var set = WriterMembers.Setters.Single(setter => setter.Serves(field.Declaration)).Set;
            WriterAction[] calls = [set(field), (ref RootWriter w) => w.SetNull(field)];

            foreach (var call in calls)
            {
                var writer = item.Writer();

                WriterAssert.Throws<ArgumentException>(ref writer, call);
                writer.Abandon();
            }
        }
    }

    [Fact]
    public void A_Get_member_called_for_a_field_of_another_type_is_ArgumentException()
    {
        using var item = EveryType.Open();
        var root = EveryType.WriteFull(item);

        foreach (var (member, get, serves) in ReaderMembers.Getters)
        {
            foreach (var field in item.EntityType.Fields.Where(field => !serves(field.Declaration)))
            {
                var exception = Record.Exception(() => get(item.Reader(root), field));

                Assert.True(exception?.GetType() == typeof(ArgumentException), $"{member} of {field.Name} threw {exception?.GetType().Name ?? "nothing"}.");
            }
        }
    }

    [Fact]
    public void Every_Get_member_and_IsNull_called_for_a_field_of_another_entity_type_are_ArgumentException()
    {
        using var item = EveryType.Open();
        Assert.True(item.Schema.TryGetEntityType("Twin", out var twin));
        var root = EveryType.WriteFull(item);

        foreach (var field in twin.Fields)
        {
            foreach (var (member, get, serves) in ReaderMembers.Getters.Where(getter => getter.Serves(field.Declaration)))
            {
                var exception = Record.Exception(() => get(item.Reader(root), field));

                Assert.True(exception?.GetType() == typeof(ArgumentException), $"{member} of {field.Name} of Twin threw {exception?.GetType().Name ?? "nothing"}.");
            }

            Assert.Throws<ArgumentException>(() => item.Reader(root).IsNull(field));
        }
    }
}
