// purpose: engine-free world coordinate so Core never references UnityEngine.Vector3
// owns:    WorldPosition
// deps:    none

namespace ShelfTransfer.Core.Gateway;

public readonly record struct WorldPosition(float X, float Y, float Z);
