namespace Contracts;

/// <summary>A point on the wire: world yards or zone map coordinates, as the carrying message says.</summary>
public readonly record struct Point3(float X, float Y, float Z);

/// <summary>A horizontal point on the wire, in world yards.</summary>
public readonly record struct Point2(float X, float Y);
