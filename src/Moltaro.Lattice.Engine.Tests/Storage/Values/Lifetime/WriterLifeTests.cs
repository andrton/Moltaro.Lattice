using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Lifetime;

// The life of a writer (brief of S05, "The life of a writer"). A field is set once: a second Set…
// or SetNull of a field that was set is InvalidOperationException. A refused call — malformed
// text, a value out of range, SetNull of a field that is not nullable — did not set the field, and
// a valid value may follow. After Complete, whether it returned or failed, and after Abandon, every
// Set… member, SetNull and Complete are InvalidOperationException. Abandon after a Complete that
// returned is InvalidOperationException as well, because the root belongs to the caller then;
// Abandon after a failed Complete, and a second Abandon, do nothing. Abandon frees everything the
// writer allocated. The constructor refuses a null arena or layout.
public sealed class WriterLifeTests
{
    [Fact]
    public void A_field_that_was_set_cannot_be_set_again()
    {
        using var item = EveryType.Open();

        foreach (var field in item.EntityType.Fields)
        {
            var writer = item.Writer();
            WriterMembers.SetValid(ref writer, field);

            WriterAssert.Throws<InvalidOperationException>(ref writer, SetterOf(field));

            // SetNull only of a nullable field: for one that is not nullable the brief does not say
            // whether the second call or the missing nullability decides.
            if (field.Declaration.IsNullable)
            {
                WriterAssert.Throws<InvalidOperationException>(ref writer, (ref RootWriter w) => w.SetNull(field));
            }

            writer.Abandon();
        }
    }

    [Fact]
    public void A_field_that_was_set_to_null_cannot_be_set_again()
    {
        using var item = EveryType.Open();

        foreach (var field in item.EntityType.Fields.Where(field => field.Declaration.IsNullable))
        {
            var writer = item.Writer();
            writer.SetNull(field);

            WriterAssert.Throws<InvalidOperationException>(ref writer, SetterOf(field));
            WriterAssert.Throws<InvalidOperationException>(ref writer, (ref RootWriter w) => w.SetNull(field));
            writer.Abandon();
        }
    }

    [Fact]
    public void A_refused_call_did_not_set_the_field_and_a_valid_value_may_follow()
    {
        using var item = EveryType.Open();
        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item, "String", "Int32");

        // Malformed text, a value out of range of an Int8, SetNull of a field that is not nullable.
        WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetString(item.Field("String"), "\uD800"));
        WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetInteger(item.Field("Int8OrNull"), 1_000));
        WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetNull(item.Field("Int32")));
        writer.SetString(item.Field("String"), "valid");
        writer.SetInteger(item.Field("Int8OrNull"), -100);
        writer.SetInteger(item.Field("Int32"), 5);
        var reader = item.Reader(writer.Complete());

        Assert.Equal("valid", reader.GetString(item.Field("String")));
        Assert.Equal(-100, reader.GetInteger(item.Field("Int8OrNull")));
        Assert.Equal(5, reader.GetInteger(item.Field("Int32")));
    }

    [Fact]
    public void After_a_Complete_that_returned_every_member_and_Abandon_are_InvalidOperationException()
    {
        using var item = EveryType.Open();
        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item);
        var root = writer.Complete();
        var after = StoredBytes.In(item.Arena);

        foreach (var call in EveryEnd(item))
        {
            WriterAssert.Throws<InvalidOperationException>(ref writer, call);
        }

        WriterAssert.Throws<InvalidOperationException>(ref writer, static (ref RootWriter w) => w.Abandon());

        // The root stays the caller's, as Complete left it.
        Assert.Equal(after, StoredBytes.In(item.Arena));
        Assert.True(ArenaBlocks.IsValid(item.Arena, root));
    }

    [Fact]
    public void After_a_failed_Complete_every_member_is_InvalidOperationException_and_Abandon_does_nothing()
    {
        using var item = EveryType.Open();
        var before = StoredBytes.In(item.Arena);
        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item, "Guid");
        writer.SetBinary(item.Field("BinaryOrNull"), new byte[100_000]);
        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.ValidationFailed);

        foreach (var call in EveryEnd(item))
        {
            WriterAssert.Throws<InvalidOperationException>(ref writer, call);
        }

        writer.Abandon();
        writer.Abandon();

        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    [Fact]
    public void After_Abandon_every_member_is_InvalidOperationException_and_a_second_Abandon_does_nothing()
    {
        using var item = EveryType.Open();
        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item);
        writer.Abandon();

        foreach (var call in EveryEnd(item))
        {
            WriterAssert.Throws<InvalidOperationException>(ref writer, call);
        }

        writer.Abandon();
    }

    [Fact]
    public void Abandon_frees_everything_the_writer_allocated()
    {
        using var item = EveryType.Open();

        // A root that stays, so that the arena holds something before the writer.
        var kept = EveryType.WriteFull(item);
        var before = StoredBytes.In(item.Arena);

        // Values of every placement, inline, in a block of their own and chunked.
        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item, "String");
        writer.SetString(item.Field("String"), new string('s', 200_000));
        writer.SetString(item.Field("StringOrNull"), new string('t', 1_000));
        writer.SetBinary(item.Field("BinaryOrNull"), new byte[70_000]);
        writer.Abandon();

        Assert.Equal(before, StoredBytes.In(item.Arena));
        Assert.True(ArenaBlocks.IsValid(item.Arena, kept));
    }

    [Fact]
    public void The_constructor_refuses_a_null_arena_or_layout()
    {
        using var item = EveryType.Open();

        Assert.Throws<ArgumentNullException>(() => { _ = new RootWriter(null!, item.Layout, RootArena.Handle, prepared: true); });
        Assert.Throws<ArgumentNullException>(() => { _ = new RootWriter(item.Arena, null!, RootArena.Handle, prepared: true); });
    }

    // The member that serves the field, with a valid value.
    private static WriterAction SetterOf(LatticeField field) =>
        WriterMembers.Setters.Single(setter => setter.Serves(field.Declaration)).Set(field);

    // Every Set… member, each for a field it serves, SetNull and Complete: the calls a writer that
    // has ended refuses.
    private static IEnumerable<WriterAction> EveryEnd(RootArena item)
    {
        foreach (var (_, set, serves) in WriterMembers.Setters)
        {
            yield return set(item.EntityType.Fields.First(field => field.Declaration.IsNullable && serves(field.Declaration)));
        }

        yield return (ref RootWriter w) => w.SetNull(item.Field("Int32OrNull"));
        yield return static (ref RootWriter w) => w.Complete();
    }
}
