using Moltaro.Lattice.Engine.Delivery;

namespace Moltaro.Lattice.Engine.Children;

// What the capture of a child starts from, taken under the lock of the writes in the hold that registers
// it (Capture.Register; the planner's decision of 17 November 2026 after the study of S11, point 2): the
// published sequence of the master, S; the slots of the root table the capture walks, those below
// SlotExtent, its fence, below which every root live at S lies; and the cursor of the child in the tail,
// from which every transaction after S is delivered to it.
internal readonly record struct CaptureStart(ulong Sequence, int SlotExtent, DeliveryCursor Cursor);
