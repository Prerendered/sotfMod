// purpose: Core's handle to one shelf; the plugin maps Key back to the live game object
// owns:    ShelfRef
// deps:    WorldPosition
// caution: Key is the only field used for identity (D2). Position is for distance/bearing display.

namespace ShelfTransfer.Core.Gateway;

public sealed record ShelfRef(string Key, string TypeName, WorldPosition Position);
