namespace Moltaro.Lattice.Engine.Entities;

// A primary key in the form the index of its entity type takes it: a number for an Int64 key, the
// text for a String key. A value, so that a lookup passes it without boxing; for an entity type of
// KeyKindEnum.Other it holds nothing, because nothing of such a type is stored.
internal readonly struct EntityKey
{
    public EntityKey(long number)
    {
        Number = number;
    }

    public EntityKey(string text)
    {
        Text = text;
    }

    public long Number { get; }

    public string? Text { get; }

    // The key for a message.
    public override string ToString() => Text ?? Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
