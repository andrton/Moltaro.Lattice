using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Binding;

// Binds the classes that hold the entities of one entity type, the class of the entity type
// and the classes of the owned types it holds, by the rule of ILatticeDatabase.Set<TEntity>():
// one table gives the type of every position, a property as well as an element, a key or a
// value of a collection.
//
// Everything is decided here, once, when a class is bound: which property holds a field, as C#
// finds it by name; whether its type is the one the table gives for the declaration, at every
// depth; how a value of it becomes what a record holds, for a write (ToRecord); and how what a
// record holds becomes a value of it, for a read (FromRecord). What is built is a tree of
// delegates, a pair for each position; nothing is looked up by reflection afterwards. Every way in
// which a class disagrees with the schema is BindingFailed, with the property named, and nothing
// here ends in another exception.
//
// What a write takes of an object is checked as a record is: a null where a position does not
// allow it, at any depth, a key that a Map holds twice under the comparison of its key
// declaration, a String or Binary value above the bound of one value at any depth (ValueBound),
// text that is not well-formed at any depth, and an integer that its enum type does not allow at
// any depth, are ValidationFailed. Of equal members of a Set the first is kept. The
// class of an owned type is bound once for every pair of owned type and class, however many
// positions hold it, so binding grows with the declarations; it ends because an owned type does not
// contain itself, and it goes no deeper than a declaration may be, see SchemaBuilder.
//
// What a read gives an object is made from the form a record holds, which the engine makes under the
// protection of reads and hands over after it (Engine.Sets.ClassCopier): FromRecord runs outside the
// protection, and it is there that the constructors and the set accessors of the classes of owned
// types run. An owned value becomes a new object of its class, every property set through the set
// accessor C# sees for it, null included; a List becomes a new List<T> or array of the type of the
// property, each element converted, an empty array the one empty array of its type, and a List
// property of the very type a record holds there is given the List the read made, which holds the same
// values at every depth. Map keys and values and Set members are converted where the bound class uses
// another .NET form. A Dictionary or HashSet keeps the schema comparer and its entry count.
// A read can hand over a collection it already made in the form the class declares.
internal sealed class ClassBinder
{
    // The conversion of a value that a record holds as the class holds it: a scalar, a String, a Binary
    // value, an Enum given as its integer and a Reference, whatever their depth.
    private static readonly Func<object, object> Same = static value => value;

    private readonly LatticeEntityType _entityType;
    private readonly Dictionary<(LatticeOwnedType OwnedType, Type Class), Conversion> _ownedClasses = [];

    public ClassBinder(LatticeEntityType entityType)
    {
        _entityType = entityType;
    }

    // An object of the class is made by its public parameterless constructor, so a class
    // without one, and an abstract class, cannot hold an entity or an owned value.
    public static ConstructorInfo Constructor(Type type, string role) =>
        !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is { } constructor
            ? constructor
            : throw BindingFailed($"{type.Name}, {role}, needs a public parameterless constructor and cannot be abstract.");

    // The property that holds a field in an object of the class, its accessors, what turns its value
    // into what a record holds and what turns what a record holds into its value.
    public BoundProperty Bind(Type type, LatticeField field, string owner)
    {
        var (property, getAccessor, setAccessor) = FindProperty(type, field, owner);
        var place = new Place(type, property, field, owner);
        var conversion = ConversionOf(property.PropertyType, field.Declaration, place);

        // A Binary property binds only as a byte[] (ConversionOf); its value is checked as ToRecord checks
        // its type, and not copied.
        var take = field.Declaration.Type == LatticeFieldTypeEnum.Binary
            ? ReadAsTheyAre(property.PropertyType, Same, place)
            : conversion.ToRecord;

        // The get accessor is called virtually, as C# calls it: an override of a derived class of the
        // bound one is the one that runs.
        var instance = Expression.Parameter(typeof(object), "instance");
        var read = Expression.Convert(Expression.Call(Expression.Convert(instance, type), getAccessor), typeof(object));
        var get = Expression.Lambda<Func<object, object?>>(read, instance).Compile();

        return new BoundProperty(field, property, getAccessor, setAccessor, get, conversion.ToRecord, take, conversion.FromRecord);
    }

    // The property of a field is the one C# finds by its name on the class: the public member of
    // that name of the most derived class that declares one. A member of a base class that it
    // hides is not considered, whatever it is; a member that is not public does not hide one, as C#
    // does not see it from outside. The member found has to be an instance property, not a field or
    // anything else: a public field that hides a property of a base class is refused, and the
    // hidden property is not bound in its place.
    //
    // The accessors are those C# sees for that property on the class: its own, and, when it
    // overrides a virtual property and declares only one accessor, the other one of the property it
    // overrides, found on the nearest base class that declares it. The get accessor is public, the
    // set accessor of any accessibility.
    private static (PropertyInfo Property, MethodInfo GetAccessor, MethodInfo SetAccessor) FindProperty(Type type, LatticeField field, string owner)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        for (var declaring = type; declaring is not null; declaring = declaring.BaseType)
        {
            var declared = declaring.GetMember(field.Name, Declared);
            if (declared.Length == 0)
            {
                continue;
            }

            if (Array.Find(declared, member => member is not PropertyInfo) is { } other)
            {
                throw BindingFailed(
                    $"The public member '{field.Name}' of {type.Name} is a {MemberKind(other)}, not a property; field '{field.Name}' of '{owner}' needs a public property of that name with get and set.");
            }

            var property = (PropertyInfo)declared[0];
            if (declared.Length > 1 || property.GetIndexParameters().Length > 0)
            {
                throw BindingFailed($"{type.Name} has an indexer named '{field.Name}'; field '{field.Name}' of '{owner}' needs a property of that name.");
            }

            if ((property.GetMethod ?? property.SetMethod)!.IsStatic)
            {
                throw BindingFailed($"Property '{field.Name}' of {type.Name} is static; field '{field.Name}' of '{owner}' needs an instance property with get and set.");
            }

            var (getAccessor, setAccessor) = AccessorsSeen(declaring, property);

            if (getAccessor is not { IsPublic: true } || setAccessor is null)
            {
                throw BindingFailed($"Property '{field.Name}' of {type.Name} has no public get or no set; field '{field.Name}' of '{owner}' needs both.");
            }

            return (property, getAccessor, setAccessor);
        }

        throw BindingFailed($"{type.Name} needs a public property '{field.Name}' with get and set, as field '{field.Name}' of '{owner}' declares.");
    }

    // The accessors C# sees for a property declared on a class: its own, and those it inherits. Only
    // an override inherits: a property that overrides a virtual one takes the accessor it does not
    // declare from the property it overrides, which is the property of that name on the nearest base
    // class that declares one. That one may be an override itself, so the search goes on until both
    // accessors are found or a property is reached that overrides nothing. A property that hides
    // one of its base class with new inherits nothing.
    private static (MethodInfo? Get, MethodInfo? Set) AccessorsSeen(Type declaring, PropertyInfo property)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        var get = property.GetMethod;
        var set = property.SetMethod;
        var current = property;

        while ((get is null || set is null) && Overrides(current))
        {
            // A base class between the two that does not override the property declares none.
            declaring = declaring.BaseType!;

            if (Array.Find(declaring.GetProperties(Declared), candidate => candidate.Name == property.Name && candidate.GetIndexParameters().Length == 0) is not { } inherited)
            {
                continue;
            }

            get ??= inherited.GetMethod;
            set ??= inherited.SetMethod;
            current = inherited;
        }

        return (get, set);
    }

    // Whether the accessors of a property override those of a base class.
    private static bool Overrides(PropertyInfo property)
    {
        var accessor = (property.GetMethod ?? property.SetMethod)!;

        return accessor.GetBaseDefinition().DeclaringType != accessor.DeclaringType;
    }

    // The kind of a member that is not a property, in a message: a field, a method, an event or a
    // nested type.
    private static string MemberKind(MemberInfo member) => member.MemberType switch
    {
        MemberTypes.Field => "field",
        MemberTypes.Method => "method",
        MemberTypes.Event => "event",
        MemberTypes.NestedType => "nested type",
        _ => member.MemberType.ToString().ToLowerInvariant(),
    };

    // The two conversions of a position of this declaration for a value of the given type, which is not
    // null: what a write takes of it, and what a read gives it of what a record holds. The type is checked
    // against the table here, once.
    private Conversion ConversionOf(Type type, LatticeValueDeclaration declaration, Place place) => declaration.Type switch
    {
        LatticeFieldTypeEnum.Owned => BindOwned(type, declaration.OwnedType!, place),
        LatticeFieldTypeEnum.List => BindList(type, declaration, place),
        LatticeFieldTypeEnum.Set => BindSet(type, declaration, place),
        LatticeFieldTypeEnum.Map => BindMap(type, declaration, place),
        LatticeFieldTypeEnum.Enum => BindEnum(type, declaration, place),
        LatticeFieldTypeEnum.Binary when type == typeof(byte[]) => new Conversion(ReadAsTheyAre(type, static value => ((byte[])value).Clone(), place), Same),
        LatticeFieldTypeEnum.Binary => throw place.Mismatch(),
        LatticeFieldTypeEnum.String or LatticeFieldTypeEnum.Reference when declaration.ScalarType == LatticeFieldTypeEnum.String && type == typeof(string) =>
            new Conversion(BindText(place), Same),
        _ when type == declaration.PositionType => new Conversion(Same, Same),
        _ => throw place.Mismatch(),
    };

    // Text is taken as it is, after one pass that refuses text that is not well-formed, a surrogate
    // without its pair, at any depth: such text is never stored and never replaced by another
    // character (docs/decisions/strings.md, "Input validity and preservation"). A String, and the key of
    // the target of a Reference whose target has a String key, which is validated as a String value is.
    private Func<object, object> BindText(Place place)
    {
        var entityTypeName = _entityType.Name;

        return value => WellFormedText.IsWellFormed((string)value)
            ? value
            : throw ValidationFailed(entityTypeName, place.Holds("text that is not well-formed: it has a surrogate without its pair"));
    }

    // An Enum is its underlying integer, as it is stored, or a .NET enum that agrees with the
    // enum type; a .NET enum value is held as its integer, and a read gives the .NET enum of the
    // integer it holds. A write refuses an integer that the enum type does not allow (EnumValues),
    // also one that a .NET enum property holds by a cast: agreeing with the enum type does not keep a
    // .NET enum from holding a value it does not name.
    private Conversion BindEnum(Type type, LatticeValueDeclaration declaration, Place place)
    {
        var enumType = declaration.EnumType!;
        var entityTypeName = _entityType.Name;

        object Allowed(object integer) => EnumValues.IsValid(enumType, integer)
            ? integer
            : throw ValidationFailed(entityTypeName, place.Holds(EnumValues.Undeclared(enumType, integer)));

        if (type == declaration.PositionType)
        {
            return new Conversion(Allowed, Same);
        }

        var dotNetEnum = declaration.IsNullable ? Nullable.GetUnderlyingType(type) : type;
        if (dotNetEnum is not { IsEnum: true })
        {
            throw place.Mismatch();
        }

        var differences = DotNetEnumAgreement.Differences(dotNetEnum, enumType);
        if (differences.Count > 0)
        {
            throw BindingFailed(
                $"Property '{place.Property.Name}' of {place.Class.Name} holds the .NET enum {dotNetEnum.Name}, which does not agree with enum type '{enumType.Name}' of the schema: {string.Join("; ", differences)}. Change the enum or the schema so that they agree.");
        }

        var stored = declaration.ValueType;

        // The .NET enum of an integer of the underlying type, as C# casts it: (TEnum)integer.
        var integer = Expression.Parameter(typeof(object), "integer");
        var cast = Expression.Convert(Expression.Convert(Expression.Convert(integer, stored), dotNetEnum), typeof(object));
        var toEnum = Expression.Lambda<Func<object, object>>(cast, integer).Compile();

        return new Conversion(value => Allowed(Convert.ChangeType(value, stored, CultureInfo.InvariantCulture)), toEnum);
    }

    // An owned value is an object of the class its owned type names. An owned type without a
    // class has no form in a class, and LatticeOwnedValue is for records only.
    private Conversion BindOwned(Type type, LatticeOwnedType ownedType, Place place)
    {
        if (ownedType.ClrTypeName is null)
        {
            throw BindingFailed(
                $"Property '{place.Property.Name}' of {place.Class.Name} holds values of owned type '{ownedType.Name}', which names no ClrType: such a value has no form in a class and is accessed through records.");
        }

        if (type == typeof(LatticeOwnedValue))
        {
            throw BindingFailed(
                $"Property '{place.Property.Name}' of {place.Class.Name} holds a LatticeOwnedValue, which only a record holds; in a class a value of owned type '{ownedType.Name}' is an object of {ownedType.ClrTypeName}.");
        }

        if (type.FullName != ownedType.ClrTypeName)
        {
            throw place.Mismatch();
        }

        if (_ownedClasses.TryGetValue((ownedType, type), out var bound))
        {
            return bound;
        }

        var constructor = Constructor(type, $"the class of owned type '{ownedType.Name}'");
        var properties = ownedType.Fields.Select(field => Bind(type, field, ownedType.Name)).ToArray();
        var entityTypeName = _entityType.Name;
        var encoding = _entityType.StringEncoding;

        // A String or Binary value of the owned value is checked against the bound of one value before it is
        // taken, so that an array above it is never copied; those of the entity are checked before
        // (EntityBinding.ValuesForWrite).
        Func<object, object> toRecord = value =>
        {
            var owned = new LatticeOwnedValue(ownedType);
            foreach (var property in properties)
            {
                var fieldValue = property.Get(value);
                if (fieldValue is not null)
                {
                    if (!ValueBound.Holds(property.Field.Declaration, fieldValue, encoding))
                    {
                        throw ValidationFailed(entityTypeName, new Place(type, property.Property, property.Field, ownedType.Name).Holds(ValueBound.Held));
                    }

                    owned.Values[property.Field.Ordinal] = property.ToRecord(fieldValue);
                }
                else if (!property.Field.Declaration.IsNullable)
                {
                    throw ValidationFailed(entityTypeName, $"property '{property.Field.Name}' of {type.Name} has no value, which field '{property.Field.Name}' of '{ownedType.Name}' requires.");
                }
            }

            return owned;
        };

        var create = Expression.Lambda<Func<object>>(Expression.New(constructor)).Compile();
        var setters = Array.ConvertAll(properties, property => Setter(type, property));

        // Every property is set, to null as well: what the constructor gave it is not what was stored.
        Func<object, object> fromRecord = value =>
        {
            var owned = (LatticeOwnedValue)value;
            var instance = create();

            for (var index = 0; index < properties.Length; index++)
            {
                var fieldValue = owned.Values[properties[index].Field.Ordinal];

                setters[index](instance, fieldValue is null ? null : properties[index].FromRecord(fieldValue));
            }

            return instance;
        };

        bound = new Conversion(toRecord, fromRecord);
        _ownedClasses.Add((ownedType, type), bound);
        return bound;
    }

    // What sets the property of an object of the class to a value of its type, or to null: the set
    // accessor C# sees for it, of any accessibility, called virtually as C# calls it.
    private static Action<object, object?> Setter(Type type, BoundProperty property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var call = Expression.Call(Expression.Convert(instance, type), property.SetAccessor, Expression.Convert(value, property.Property.PropertyType));

        return Expression.Lambda<Action<object, object?>>(call, instance, value).Compile();
    }

    // A List is a List<T> or an array of T. A read gives a List property of the very type a record
    // holds there the List the read made.
    private Conversion BindList(Type type, LatticeValueDeclaration declaration, Place place)
    {
        var isArray = type.IsSZArray;
        var elementType = isArray ? type.GetElementType()! : GenericArgument(type, typeof(List<>)) ?? throw place.Mismatch();
        var elementDeclaration = declaration.Element!;
        var element = ConversionOf(elementType, elementDeclaration, place);
        var toRecord = BindSequence(element.ToRecord, declaration, place);
        var sameElements = elementType == elementDeclaration.PositionType;

        if (isArray)
        {
            var fromArray = sameElements
                ? SequenceMethod(nameof(CopyArray), elementType).CreateDelegate<Func<object, object>>()
                : SequenceFromRecord(nameof(ArrayOf), elementType, element.FromRecord);

            return new Conversion(ReadAsTheyAre(type, toRecord, place), fromArray);
        }

        var fromRecord = sameElements ? Same : SequenceFromRecord(nameof(ListOf), elementType, element.FromRecord);

        return new Conversion(toRecord, fromRecord);
    }

    // A property of an array type, or an array element of one, may hold an array of another
    // element type at run time, which the runtime lets stand for it. It is taken or refused by
    // the rule of ArrayElements: an array of a class derived from the declared one is taken,
    // and an array whose elements would be read as other values, such as a uint[] in an int[]
    // property, is refused when it is written, as a record refuses it when it is set.
    private Func<object, object> ReadAsTheyAre(Type arrayType, Func<object, object> toRecord, Place place)
    {
        var entityTypeName = _entityType.Name;
        var declaredElementType = arrayType.GetElementType()!;

        return value => value.GetType() == arrayType || ArrayElements.AreReadAsTheyAre(value.GetType(), declaredElementType)
            ? toRecord(value)
            : throw ValidationFailed(entityTypeName, place.Holds($"a {Place.TypeName(value.GetType())} where {Place.TypeName(arrayType)} is declared"));
    }

    private Conversion BindSet(Type type, LatticeValueDeclaration declaration, Place place)
    {
        var elementType = GenericArgument(type, typeof(HashSet<>)) ?? throw place.Mismatch();
        var element = ConversionOf(elementType, declaration.Element!, place);
        var toRecord = BindSequence(element.ToRecord, declaration, place);
        if (type == declaration.ValueType)
        {
            return new Conversion(toRecord, Same);
        }

        var fromRecord = SequenceMethod(nameof(SetOf), elementType)
            .CreateDelegate<Func<object, int, Func<object, object>, TextComparison?, object>>();
        var form = declaration.Sequence!;
        var comparison = declaration.Element!.TextComparison;

        return new Conversion(toRecord, value => fromRecord(value, form.Count(value), element.FromRecord, comparison));
    }

    // An element that is a String or a Binary value is checked against the bound of one value before it is
    // taken.
    private Func<object, object> BindSequence(Func<object, object> elementToRecord, LatticeValueDeclaration declaration, Place place)
    {
        var element = declaration.Element!;
        var bounded = ValueBound.Applies(element);
        var form = declaration.Sequence!;
        var entityTypeName = _entityType.Name;
        var encoding = _entityType.StringEncoding;

        return value =>
        {
            var collection = form.Create();
            foreach (var item in (IEnumerable)value)
            {
                if (item is not null)
                {
                    if (bounded && !ValueBound.Holds(element, item, encoding))
                    {
                        throw ValidationFailed(entityTypeName, place.Holds(ValueBound.Held));
                    }

                    form.Add(collection, elementToRecord(item));
                }
                else if (element.IsNullable)
                {
                    form.Add(collection, null);
                }
                else
                {
                    throw ValidationFailed(entityTypeName, place.Holds("a null where its declaration does not allow one"));
                }
            }

            return collection;
        };
    }

    // What a read gives a List property whose type is not the List<T> a record holds there: a new List or
    // array, made by ListOf or ArrayOf for the element type of the property, of every element of the
    // List of the record converted, a null kept. The generic method is made once, here.
    private static Func<object, object> SequenceFromRecord(string method, Type elementType, Func<object, object> element)
    {
        var convert = SequenceMethod(method, elementType).CreateDelegate<Func<object, Func<object, object>, object>>();

        return value => convert(value, element);
    }

    private static MethodInfo SequenceMethod(string method, Type elementType) =>
        typeof(ClassBinder).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(elementType);

    private static object ListOf<TElement>(object list, Func<object, object> element)
    {
        var source = (IList)list;
        var result = new List<TElement>(source.Count);

        foreach (var item in source)
        {
            result.Add(item is null ? default! : (TElement)element(item));
        }

        return result;
    }

    private static object SetOf<TElement>(object set, int count, Func<object, object> element, TextComparison? comparison)
    {
        var result = new HashSet<TElement>(count, comparison as IEqualityComparer<TElement>);
        foreach (var value in (IEnumerable)set)
        {
            result.Add(value is null ? default! : (TElement)element(value));
        }

        return result;
    }

    // Elements of the same type are copied without boxing each element.
    private static object CopyArray<TElement>(object list)
    {
        var source = (List<TElement>)list;

        return source.Count == 0 ? Array.Empty<TElement>() : source.ToArray();
    }

    // An empty List is the one empty array of the element type, Array.Empty, as an empty Binary value is:
    // no caller can change it, so every read gives the same one.
    private static object ArrayOf<TElement>(object list, Func<object, object> element)
    {
        var source = (IList)list;

        if (source.Count == 0)
        {
            return Array.Empty<TElement>();
        }

        var result = new TElement[source.Count];

        for (var index = 0; index < result.Length; index++)
        {
            result[index] = source[index] is { } item ? (TElement)element(item) : default!;
        }

        return result;
    }

    // A Map is a Dictionary<K, V>. Its keys are added under the comparer of the key
    // declaration, not under the comparer of the Dictionary.
    private Conversion BindMap(Type type, LatticeValueDeclaration declaration, Place place)
    {
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Dictionary<,>))
        {
            throw place.Mismatch();
        }

        var arguments = type.GetGenericArguments();
        var keyDeclaration = declaration.Key!;
        var valueDeclaration = declaration.Value!;
        var key = ConversionOf(arguments[0], keyDeclaration, place);
        var item = ConversionOf(arguments[1], valueDeclaration, place);
        var boundedKey = ValueBound.Applies(keyDeclaration);
        var boundedValue = ValueBound.Applies(valueDeclaration);
        var form = declaration.Map!;
        var entityTypeName = _entityType.Name;
        var encoding = _entityType.StringEncoding;

        object ToRecord(object value)
        {
            var map = form.Create();
            foreach (DictionaryEntry entry in (IDictionary)value)
            {
                if (entry.Key is null)
                {
                    throw ValidationFailed(entityTypeName, place.Holds("a null key where a Map needs a key"));
                }

                if (boundedKey && !ValueBound.Holds(keyDeclaration, entry.Key, encoding))
                {
                    throw ValidationFailed(entityTypeName, place.Holds(ValueBound.Held));
                }

                object? itemValue = null;
                if (entry.Value is not null)
                {
                    if (boundedValue && !ValueBound.Holds(valueDeclaration, entry.Value, encoding))
                    {
                        throw ValidationFailed(entityTypeName, place.Holds(ValueBound.Held));
                    }

                    itemValue = item.ToRecord(entry.Value);
                }
                else if (!valueDeclaration.IsNullable)
                {
                    throw ValidationFailed(entityTypeName, place.Holds("a null value where its declaration does not allow one"));
                }

                if (!form.TryAdd(map, key.ToRecord(entry.Key), itemValue))
                {
                    throw ValidationFailed(entityTypeName, place.Holds($"the key '{entry.Key}' twice, as its key declaration compares keys"));
                }
            }

            return map;
        }

        if (type == declaration.ValueType)
        {
            return new Conversion(ToRecord, Same);
        }

        var fromRecord = typeof(ClassBinder).GetMethod(nameof(MapOf), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(arguments).CreateDelegate<Func<object, Func<object, object>, Func<object, object>, TextComparison?, object>>();
        var comparison = keyDeclaration.TextComparison;

        return new Conversion(ToRecord, value => fromRecord(value, key.FromRecord, item.FromRecord, comparison));
    }

    private static object MapOf<TKey, TValue>(object map, Func<object, object> key, Func<object, object> value, TextComparison? comparison)
        where TKey : notnull
    {
        var source = (IDictionary)map;
        var result = new Dictionary<TKey, TValue>(source.Count, comparison as IEqualityComparer<TKey>);
        foreach (DictionaryEntry entry in source)
        {
            result.Add((TKey)key(entry.Key), entry.Value is null ? default! : (TValue)value(entry.Value));
        }

        return result;
    }

    private static Type? GenericArgument(Type type, Type definition) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == definition ? type.GetGenericArguments()[0] : null;

    private static LatticeException BindingFailed(string message) => new(LatticeErrorEnum.BindingFailed, message);

    private static LatticeException ValidationFailed(string entityTypeName, string message) =>
        new(LatticeErrorEnum.ValidationFailed, $"{entityTypeName}: {message}");

    // What a write takes of a value of a position and what a read gives a position of what a record holds,
    // each for a value that is not null.
    private readonly record struct Conversion(Func<object, object> ToRecord, Func<object, object> FromRecord);

    // A property that a field is bound to, for the messages about it: at binding, what type it
    // needs; at a write, what it holds.
    private readonly record struct Place(Type Class, PropertyInfo Property, LatticeField Field, string Owner)
    {
        public LatticeException Mismatch() => BindingFailed(
            $"{Class.Name} needs a public property '{Property.Name}' of {Needs(Field.Declaration)} with get and set, as field '{Field.Name}' of '{Owner}' declares; it is {TypeName(Property.PropertyType)}.");

        public string Holds(string what) => $"property '{Property.Name}' of {Class.Name} holds {what}, at a position of field '{Field.Name}' of '{Owner}'.";

        // The type the table gives for a declaration, in words.
        private static string Needs(LatticeValueDeclaration declaration) => declaration.Type switch
        {
            LatticeFieldTypeEnum.Owned => declaration.OwnedType!.ClrTypeName ?? $"a class of owned type '{declaration.OwnedType.Name}'",
            LatticeFieldTypeEnum.Enum => $"{TypeName(declaration.PositionType)} or a .NET enum that agrees with enum type '{declaration.EnumType!.Name}'",
            LatticeFieldTypeEnum.List => $"List<{Needs(declaration.Element!)}> or {Needs(declaration.Element!)}[]",
            LatticeFieldTypeEnum.Set => $"HashSet<{Needs(declaration.Element!)}>",
            LatticeFieldTypeEnum.Map => $"Dictionary<{Needs(declaration.Key!)}, {Needs(declaration.Value!)}>",
            _ => TypeName(declaration.PositionType),
        };

        // A .NET type as C# writes it: Int32?, List<Int64>, Int64[], and Int64[,] for an array
        // of two dimensions, which is not a List.
        public static string TypeName(Type type)
        {
            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                return $"{TypeName(underlying)}?";
            }

            if (type.IsSZArray)
            {
                return $"{TypeName(type.GetElementType()!)}[]";
            }

            if (type.IsArray)
            {
                // An array of one dimension whose lower bound need not be zero is written [*].
                var dimensions = type.GetArrayRank() == 1 ? "*" : new string(',', type.GetArrayRank() - 1);
                return $"{TypeName(type.GetElementType()!)}[{dimensions}]";
            }

            if (!type.IsGenericType)
            {
                return type.Name;
            }

            var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
            var name = tick < 0 ? type.Name : type.Name[..tick];
            return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
        }
    }
}
