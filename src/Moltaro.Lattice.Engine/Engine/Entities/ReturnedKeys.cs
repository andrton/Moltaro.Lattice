using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// The keys one enumeration has returned, which it remembers for the length of its pass so that it
// returns none twice (the remarks of ILatticeSet<TEntity>.Enumerate; milestone contract, "Index
// preparation and traversal": a set of the identities already visited, the key text charged too).
// Managed memory of the enumeration alone (EntityPass).
//
// What is remembered is the key, because the key is what identifies an entity across everything a
// pass can meet: an entity deleted and created again under its key has another lifetime, often in
// another root entry or under another generation of it, and a growth of the primary index places
// every entry anew. Keys equal under the comparison of their field are one key, so a String key
// created again under another spelling that a comparison without case holds equal is not returned
// again. An integer key is held as its 64 bits, as EntityKey holds them; a Guid as its 16 bytes; a
// String key as its text, copied out of the root as UTF-16 code units, so that it owns its memory and
// points into none of the engine's.
//
// The places. An open-addressing table with linear probing, a power of two of places, at most three
// quarters of them taken; it doubles when a key would take more. A key goes to the place the low bits
// of its hash give, or on to the next places while they are taken. The hash is the one of the primary
// index, which a step takes from the entry of the index it found, so a step hashes no key; a growth
// of the table hashes the integer and Guid keys again, by the function of the index
// (EntityTable.IntegerHash, GuidHash), and finds the hash of a String key in its place. A place is one
// word for an integer key, the key itself, and two for a Guid, its two halves; a place of 0 is empty,
// and the key whose words are all 0, the integer 0 or the empty Guid, is held apart by a flag. A place
// of a String key is two words: its hash and its length in code units, then where its text lies, plus
// one, so that no place of a key is empty.
//
// The text. The code units of the keys one after the other in chunks, each key whole in one chunk:
// the first of FirstChunk code units, each next one twice the last up to MostChunk, and one as long
// as a key that does not fit that. A key that does not fit what is left of the last chunk begins a
// new one, and the rest of the last one stays unused.
//
// What the memory limit of a read counts (LatticeDatabaseOptions.ReadMemoryLimit), by one plain rule:
// the bytes of the elements of the arrays this holds, as they are allocated: 8 bytes a place for an
// integer key and 16 for a Guid or a String key, 2 bytes a code unit of the chunks, and 8 bytes a
// reference of the list of chunks. The headers of the arrays and of the objects are not counted. So
// with n keys the places take 10.7 to 21.3 bytes a key for an integer key and 21.3 to 42.7 for a Guid
// or a String key, as the table fills between two doublings, and a String key takes besides 2 bytes
// a code unit of its text, with at most one chunk unused at the end of each.
//
// Staged, then committed. A step stages the key of the entity it is about to return (Stage), after
// it has made room for it, and the enumeration commits it (Commit) once the entity is built: a build
// that fails in the code of the application, a constructor or a setter, leaves the key unremembered
// and the entity to a later step. Staging again replaces what was staged and not committed, and the
// next key's text is written over the text of a key staged and not committed.
internal sealed class ReturnedKeys
{
    // The places of the first table.
    private const int FirstPlaces = 16;

    // The code units of the first chunk of text, and of a chunk at most but for a key longer than that.
    private const int FirstChunk = 256;

    private const int MostChunk = 32 * 1024;

    // The table of the entity type, which knows how a key is stored and hashed.
    private readonly EntityTable _table;

    private readonly KeyKindEnum _kind;

    private readonly LatticeField _key;

    // The words of a place: 1 for an integer key, 2 for a Guid or a String key.
    private readonly int _wordsPerPlace;

    // The places, empty until the first key is staged.
    private long[] _places = [];

    // The keys in the places.
    private int _count;

    // Whether the key whose words are all 0 has been returned; it has no place.
    private bool _holdsZero;

    // The chunks of text, the first _chunkCount of them in use, and the code units used of the last.
    private char[][] _chunks = [];

    private int _chunkCount;

    private int _used;

    // The bytes of the chunks and of their list.
    private long _textBytes;

    // The key staged and not committed yet: its words and hash, and for a String key its length.
    private long _first;

    private long _second;

    private uint _hash;

    private int _length;

    public ReturnedKeys(EntityTable table)
    {
        _table = table;
        _kind = table.KeyKind;
        _key = table.EntityType.Key;
        _wordsPerPlace = _kind == KeyKindEnum.Integer ? 1 : 2;
    }

    // The bytes this holds, by the rule of the class.
    public long Bytes => ((long)_places.Length * sizeof(long)) + _textBytes;

    // Whether the key the root stores has been returned. The hash is the one of the entry of the index
    // that led to the root. Inside the protection of reads, as the root reader is.
    public bool Contains(uint hash, in RootReader stored)
    {
        if (_kind == KeyKindEnum.Text)
        {
            return FindText(hash, stored);
        }

        var (first, second) = WordsOf(stored);

        return first == 0 && second == 0 ? _holdsZero : Find(hash, first, second);
    }

    // The bytes this would hold with room for the key the root stores, which it does not hold: the
    // doubled places when the key would take more than three quarters of them, and for a String key a
    // new chunk when its text does not fit the last one. Allocates nothing.
    public long BytesWithRoomFor(in RootReader stored)
    {
        var bytes = (long)PlacesFor(_count + 1) * _wordsPerPlace * sizeof(long);

        if (_kind != KeyKindEnum.Text)
        {
            return bytes;
        }

        var bound = _table.TextBound(stored);

        if (FitsLastChunk(bound))
        {
            return bytes + _textBytes;
        }

        var list = _chunkCount < _chunks.Length ? 0 : (long)(Math.Max(1, 2 * _chunks.Length) - _chunks.Length) * sizeof(long);

        return bytes + _textBytes + (2L * NextChunkLength(bound)) + list;
    }

    // Stages the key the root stores, which this does not hold: makes room for it, allocating what
    // BytesWithRoomFor counts, and copies a String key's text into the last chunk. Inside the
    // protection of reads.
    public void Stage(uint hash, in RootReader stored)
    {
        Grow();
        _hash = hash;

        if (_kind == KeyKindEnum.Text)
        {
            StageText(hash, stored);
        }
        else
        {
            (_first, _second) = WordsOf(stored);
        }
    }

    // Remembers the key staged last; once for each Stage.
    public void Commit()
    {
        if (_kind == KeyKindEnum.Text)
        {
            _used += _length;
        }
        else if (_first == 0 && _second == 0)
        {
            _holdsZero = true;

            return;
        }

        Place(_places, _hash, _first, _second);
        _count++;
    }

    // Lets go of every key: for an enumeration that failed and returns nothing more.
    public void Clear()
    {
        _places = [];
        _count = 0;
        _holdsZero = false;
        _chunks = [];
        _chunkCount = 0;
        _used = 0;
        _textBytes = 0;
    }

    // The words of the integer or Guid key the root stores: the integer and 0, or the two halves of the
    // Guid.
    private (long First, long Second) WordsOf(in RootReader stored)
    {
        if (_kind == KeyKindEnum.Integer)
        {
            return (_table.StoredInteger(stored), 0);
        }

        var guid = stored.GetGuid(_key);
        ref var halves = ref Unsafe.As<Guid, long>(ref guid);

        return (halves, Unsafe.Add(ref halves, 1));
    }

    // The places a table needs for count keys: the current ones while count takes at most three
    // quarters of them, otherwise twice as many, and FirstPlaces for the first key.
    private int PlacesFor(int count)
    {
        var places = _places.Length / _wordsPerPlace;

        if (places == 0)
        {
            return FirstPlaces;
        }

        return count <= places / 4 * 3 ? places : 2 * places;
    }

    // Whether the integer or Guid key with the words, which are not both 0, has a place.
    private bool Find(uint hash, long first, long second)
    {
        var mask = (_places.Length / _wordsPerPlace) - 1;

        if (mask < 0)
        {
            return false;
        }

        for (var place = (int)(hash & mask); ; place = (place + 1) & mask)
        {
            var word = place * _wordsPerPlace;
            var held = _places[word];
            var heldSecond = _wordsPerPlace == 1 ? 0 : _places[word + 1];

            if (held == first && heldSecond == second)
            {
                return true;
            }

            if (held == 0 && heldSecond == 0)
            {
                return false;
            }
        }
    }

    // Whether the String key the root stores has a place: one of the same hash whose text the
    // comparison of the key field holds equal to the stored one.
    private bool FindText(uint hash, in RootReader stored)
    {
        var mask = (_places.Length / 2) - 1;

        if (mask < 0)
        {
            return false;
        }

        for (var place = (int)(hash & mask); ; place = (place + 1) & mask)
        {
            var head = _places[2 * place];
            var location = _places[(2 * place) + 1];

            if (location == 0)
            {
                return false;
            }

            if ((uint)((ulong)head >> 32) == hash && ValueComparer.TextEquals(stored, _key, TextAt(head, location)))
            {
                return true;
            }
        }
    }

    // The text of a place of a String key.
    private ReadOnlySpan<char> TextAt(long head, long location)
    {
        var length = (int)(uint)head;

        if (length == 0)
        {
            return [];
        }

        var at = location - 1;

        return _chunks[(int)(at >> 32)].AsSpan((int)(uint)at, length);
    }

    // Doubles the places when one more key would take more than three quarters of them, and places
    // every key again by its hash.
    private void Grow()
    {
        var places = PlacesFor(_count + 1);

        if (places * _wordsPerPlace == _places.Length)
        {
            return;
        }

        var grown = new long[places * _wordsPerPlace];

        for (var word = 0; word < _places.Length; word += _wordsPerPlace)
        {
            var first = _places[word];
            var second = _wordsPerPlace == 1 ? 0 : _places[word + 1];

            if (first != 0 || second != 0)
            {
                Place(grown, HashOfPlace(first, second), first, second);
            }
        }

        _places = grown;
    }

    // The hash a key in a place was placed by: for an integer or a Guid key the function of the
    // primary index, for a String key the one its place holds.
    private uint HashOfPlace(long first, long second) => _kind switch
    {
        KeyKindEnum.Integer => _table.IntegerHash(first),
        KeyKindEnum.Guid => _table.GuidHash(first, second),
        _ => (uint)((ulong)first >> 32),
    };

    // Writes the words of a key into the first empty place from the one its hash gives.
    private void Place(long[] places, uint hash, long first, long second)
    {
        var mask = (places.Length / _wordsPerPlace) - 1;
        var place = (int)(hash & mask);

        while (places[place * _wordsPerPlace] != 0 || (_wordsPerPlace == 2 && places[(place * _wordsPerPlace) + 1] != 0))
        {
            place = (place + 1) & mask;
        }

        places[place * _wordsPerPlace] = first;

        if (_wordsPerPlace == 2)
        {
            places[(place * _wordsPerPlace) + 1] = second;
        }
    }

    // Copies the text of the stored key into the last chunk after the code units in use, beginning a
    // new chunk when it does not fit there. One read takes the whole text, since the room is at least
    // the bound of the text (EntityTable.TextBound).
    private void StageText(uint hash, in RootReader stored)
    {
        var bound = _table.TextBound(stored);
        var length = 0;
        var location = 0L;

        if (bound > 0)
        {
            if (!FitsLastChunk(bound))
            {
                AddChunk(NextChunkLength(bound));
            }

            length = stored.ReadText(_key).Read(_chunks[_chunkCount - 1].AsSpan(_used, bound));
            location = ((long)(_chunkCount - 1) << 32) | (uint)_used;
        }

        _length = length;
        _first = (long)(((ulong)hash << 32) | (uint)length);
        _second = location + 1;
    }

    private bool FitsLastChunk(int bound) => bound == 0 || (_chunkCount > 0 && _chunks[_chunkCount - 1].Length - _used >= bound);

    // The code units of the next chunk: twice the last, from FirstChunk up to MostChunk, and at least
    // the bound of the key that needs it.
    private int NextChunkLength(int bound)
    {
        var length = _chunkCount == 0 ? FirstChunk : Math.Min(2 * _chunks[_chunkCount - 1].Length, MostChunk);

        return Math.Max(length, bound);
    }

    private void AddChunk(int length)
    {
        if (_chunkCount == _chunks.Length)
        {
            var list = new char[Math.Max(1, 2 * _chunks.Length)][];

            Array.Copy(_chunks, list, _chunkCount);
            _textBytes += (long)(list.Length - _chunks.Length) * sizeof(long);
            _chunks = list;
        }

        _chunks[_chunkCount++] = new char[length];
        _textBytes += 2L * length;
        _used = 0;
    }
}
