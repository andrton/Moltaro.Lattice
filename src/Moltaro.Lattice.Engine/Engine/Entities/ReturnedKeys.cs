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
// How many keys. The table of root entries admits fewer than 2^31 entities, so a pass remembers fewer
// than 2^31 keys, and three quarters of 2^32 places hold them all. Every count of keys and places is
// a long, and the places lie in pages of PagePlaces places, each its own array: at most 2^32 / 2^20 =
// 4,096 pages of at most 2^21 words, far below Array.MaxLength, so no array is cut short by its
// largest length and no arithmetic overflows (PlacesFor, PagesFor). A table of PagePlaces places or
// fewer is one page of just its places. The text needs no such care: a chunk is one key's text at
// most, which a value bounds to fewer than 2^28 code units, and its list grows by doubling far below
// the largest length.
//
// Room is made outside the protection of reads. A step only asks whether a key has room (HasRoomFor);
// when it has none, the step leaves the protection, the pass makes the room (MakeRoom), which doubles
// the places or adds a chunk, and the step enters again and reads the entry anew (EntityTable). So
// nothing large is allocated while a read holds back what writes retire.
//
// What the memory limit of a read counts (LatticeDatabaseOptions.ReadMemoryLimit), by one plain rule:
// the bytes of the elements of the arrays this holds, as they are allocated: 8 bytes a place for an
// integer key and 16 for a Guid or a String key, 2 bytes a code unit of the chunks, and 8 bytes a
// reference of the list of pages and of the list of chunks. The headers of the arrays and of the
// objects are not counted. So with n keys the places take 10.7 to 21.3 bytes a key for an integer key
// and 21.3 to 42.7 for a Guid or a String key, as the table fills between two doublings, and a String
// key takes besides 2 bytes a code unit of its text, with at most one chunk unused at the end of
// each. That is all the temporary memory an enumeration holds today: it takes one entry of the index
// at a time and holds no candidates and no other workspace, which come with the search of an index.
// Room is counted before it is made, with everything live at once while it is made (decision on
// queries, docs/decisions/queries.md: "sum simultaneously live buffers", "check before growing"): what
// this holds and what MakeRoom allocates beside it, since the places a doubling replaces stay live
// until every key has moved into the new ones, and the list of chunks a longer one replaces until its
// references are copied.
//
// Staged, then committed. A step stages the key of the entity it is about to return (Stage), which
// copies a String key's text into the room made for it, and the enumeration commits it (Commit) once
// the entity is built: a build that fails in the code of the application, a constructor or a setter,
// leaves the key unremembered and the entity to a later step. Staging again replaces what was staged
// and not committed, and the next key's text is written over the text of a key staged and not
// committed.
internal sealed class ReturnedKeys
{
    // The places of the first table.
    private const long FirstPlaces = 16;

    // The places of a page, as a power of two: 2^20, 8 MiB of one-word places or 16 MiB of two-word
    // ones.
    private const int PageBits = 20;

    private const long PagePlaces = 1L << PageBits;

    private const long PageMask = PagePlaces - 1;

    // The code units of the first chunk of text, and of a chunk at most but for a key longer than that.
    private const int FirstChunk = 256;

    private const int MostChunk = 32 * 1024;

    // The table of the entity type, which knows how a key is stored and hashed.
    private readonly EntityTable _table;

    private readonly KeyKindEnum _kind;

    private readonly LatticeField _key;

    // The words of a place: 1 for an integer key, 2 for a Guid or a String key.
    private readonly int _wordsPerPlace;

    // The pages of the places, empty until the first key has room, and how many places they hold: 0
    // or a power of two.
    private long[][] _pages = [];

    private long _places;

    // The keys in the places.
    private long _count;

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

    // The places a table needs for count keys when it has places now: those while count takes at most
    // three quarters of them, otherwise twice as many, and FirstPlaces for the first key. In longs:
    // fewer than 2^31 keys need at most 2^32 places (see the class).
    public static long PlacesFor(long count, long places)
    {
        if (places == 0)
        {
            return FirstPlaces;
        }

        return count <= places / 4 * 3 ? places : 2 * places;
    }

    // The pages that hold the places, a power of two of them, and the words of each page.
    public static (int Pages, int WordsPerPage) PagesFor(long places, int wordsPerPlace) =>
        places <= PagePlaces
            ? (1, (int)places * wordsPerPlace)
            : ((int)(places >> PageBits), (int)PagePlaces * wordsPerPlace);

    // The bytes of the pages of the places and of their list, by the rule of the class.
    public static long PlaceBytes(long places, int wordsPerPlace) =>
        (places * wordsPerPlace * sizeof(long)) + ((long)PagesFor(places, wordsPerPlace).Pages * sizeof(long));

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

    // Whether the key the root stores, which this does not hold, can be staged without allocating: a
    // place within three quarters of the places, and for a String key room in the last chunk for its
    // text, whose bound it gives (EntityTable.TextBound); the bound is 0 for any other key. Inside the
    // protection of reads.
    public bool HasRoomFor(in RootReader stored, out int bound)
    {
        bound = _kind == KeyKindEnum.Text ? _table.TextBound(stored) : 0;

        return PlacesFor(_count + 1, _places) == _places && FitsLastChunk(bound);
    }

    // The bytes live while MakeRoom makes room for one more key whose text takes at most bound code
    // units: what this holds and what MakeRoom allocates beside it (see the class). Allocates nothing.
    public long BytesWithRoomFor(int bound)
    {
        var places = PlacesFor(_count + 1, _places);
        var bytes = _textBytes + (_places == 0 ? 0 : PlaceBytes(_places, _wordsPerPlace));

        if (places != _places)
        {
            bytes += PlaceBytes(places, _wordsPerPlace);
        }

        if (FitsLastChunk(bound))
        {
            return bytes;
        }

        var list = _chunkCount < _chunks.Length ? 0 : (long)Math.Max(1, 2 * _chunks.Length) * sizeof(long);

        return bytes + (2L * NextChunkLength(bound)) + list;
    }

    // Makes room for one more key whose text takes at most bound code units, allocating what
    // BytesWithRoomFor counts beside what this holds: doubles the places, placing every key again by
    // its hash, when one more key would take more than three quarters of them, and adds a chunk when the
    // text does not fit the last one. Outside the protection of reads (see the class).
    public void MakeRoom(int bound)
    {
        var places = PlacesFor(_count + 1, _places);

        if (places != _places)
        {
            Grow(places);
        }

        if (!FitsLastChunk(bound))
        {
            AddChunk(NextChunkLength(bound));
        }
    }

    // Stages the key the root stores, which this does not hold and has room for, with the bound
    // HasRoomFor gave: copies a String key's text into the last chunk. Allocates nothing. Inside the
    // protection of reads.
    public void Stage(uint hash, in RootReader stored, int bound)
    {
        _hash = hash;

        if (_kind == KeyKindEnum.Text)
        {
            StageText(hash, stored, bound);
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

        Place(_pages, _places, _hash, _first, _second);
        _count++;
    }

    // Lets go of every key: for an enumeration that failed and returns nothing more.
    public void Clear()
    {
        _pages = [];
        _places = 0;
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

    // The page that holds a place, and the index of the first word of the place in it.
    private long[] PageOf(long[][] pages, long place, out int word)
    {
        word = (int)(place & PageMask) * _wordsPerPlace;

        return pages[(int)(place >> PageBits)];
    }

    // Whether the integer or Guid key with the words, which are not both 0, has a place.
    private bool Find(uint hash, long first, long second)
    {
        var mask = _places - 1;

        if (mask < 0)
        {
            return false;
        }

        for (var place = hash & mask; ; place = (place + 1) & mask)
        {
            var page = PageOf(_pages, place, out var word);
            var held = page[word];
            var heldSecond = _wordsPerPlace == 1 ? 0 : page[word + 1];

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
        var mask = _places - 1;

        if (mask < 0)
        {
            return false;
        }

        for (var place = hash & mask; ; place = (place + 1) & mask)
        {
            var page = PageOf(_pages, place, out var word);
            var head = page[word];
            var location = page[word + 1];

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

    // Moves every key into new pages of the given places, by its hash.
    private void Grow(long places)
    {
        var (pageCount, wordsPerPage) = PagesFor(places, _wordsPerPlace);
        var grown = new long[pageCount][];

        for (var page = 0; page < grown.Length; page++)
        {
            grown[page] = new long[wordsPerPage];
        }

        for (var place = 0L; place < _places; place++)
        {
            var page = PageOf(_pages, place, out var word);
            var first = page[word];
            var second = _wordsPerPlace == 1 ? 0 : page[word + 1];

            if (first != 0 || second != 0)
            {
                Place(grown, places, HashOfPlace(first, second), first, second);
            }
        }

        _pages = grown;
        _places = places;
    }

    // The hash a key in a place was placed by: for an integer or a Guid key the function of the
    // primary index, for a String key the one its place holds.
    private uint HashOfPlace(long first, long second) => _kind switch
    {
        KeyKindEnum.Integer => _table.IntegerHash(first),
        KeyKindEnum.Guid => _table.GuidHash(first, second),
        _ => (uint)((ulong)first >> 32),
    };

    // Writes the words of a key into the first empty place of the pages from the one its hash gives.
    private void Place(long[][] pages, long places, uint hash, long first, long second)
    {
        var mask = places - 1;

        for (var place = hash & mask; ; place = (place + 1) & mask)
        {
            var page = PageOf(pages, place, out var word);

            if (page[word] == 0 && (_wordsPerPlace == 1 || page[word + 1] == 0))
            {
                page[word] = first;

                if (_wordsPerPlace == 2)
                {
                    page[word + 1] = second;
                }

                return;
            }
        }
    }

    // Copies the text of the stored key into the last chunk after the code units in use, where room
    // was made for its bound. One read takes the whole text, since the room is at least its bound
    // (EntityTable.TextBound).
    private void StageText(uint hash, in RootReader stored, int bound)
    {
        var length = 0;
        var location = 0L;

        if (bound > 0)
        {
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
        var length = FirstChunk;

        if (_chunkCount > 0)
        {
            var last = _chunks[_chunkCount - 1].Length;

            length = last >= MostChunk / 2 ? MostChunk : 2 * last;
        }

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
