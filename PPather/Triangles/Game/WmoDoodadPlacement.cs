using System.Numerics;

namespace Wmo;

/// <summary>A MODS range into the root's MODD table.</summary>
public readonly record struct WmoDoodadSet(uint First, uint Count);

/// <summary>
/// One WMO-local MODD transform. Position and rotation use the unmodified
/// client coordinate basis; the triangle extractor converts to navmesh space.
/// </summary>
public readonly record struct WmoDoodadPlacement(
    uint FileDataId, string? Path, Vector3 Position, Quaternion Rotation, float Scale);
