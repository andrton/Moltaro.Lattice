using System.Reflection;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

// A field bound to the property of a class that holds it: the property, how to read it from an
// object of the class, and how a value of it that is not null becomes what a record holds,
// checked against the declaration of the field at every depth. Made by ClassBinder.
internal readonly record struct BoundProperty(
    LatticeField Field,
    PropertyInfo Property,
    Func<object, object?> Get,
    Func<object, object> ToRecord);
