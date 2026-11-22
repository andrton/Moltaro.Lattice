using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Entities;

// What a reservation of the changes of one publication that ended KeyTaken found
// (ChangePublisher.TryReserve), read right after that outcome and at no other time: the place of the change
// that fails among the changes given, the place of the unique index among the indexes of its entity type,
// and the root entry of the published root of another entity that holds the value; default when the value
// is claimed by two changes of the publication and by no published root.
internal readonly record struct TakenValue(int Change, int Index, RootHandle Owner);
