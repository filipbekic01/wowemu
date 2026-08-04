using System.Globalization;

namespace WowEmu.Core;

/// <summary>
/// A point in a map's coordinate space, plus a facing.
/// </summary>
/// <remarks>
/// Port of <c>src/server/game/Entities/Object/Position.{h,cpp}</c>. A mutable struct on purpose:
/// movement code relocates positions in place millions of times per tick, and every one of those
/// would otherwise be a copy.
/// <para>
/// Orientation is radians in <c>[0, 2π)</c> and is normalized on construction and on every
/// relocate. Skipping that normalization is the classic source of NPCs facing backwards after a
/// negative turn.
/// </para>
/// </remarks>
public struct Position(float x = 0f, float y = 0f, float z = 0f, float orientation = 0f)
    : IEquatable<Position>
{
    private float _orientation = NormalizeOrientation(orientation);

    public float X { get; set; } = x;

    public float Y { get; set; } = y;

    public float Z { get; set; } = z;

    /// <summary>Facing, in radians, always normalized to <c>[0, 2π)</c>.</summary>
    public float Orientation
    {
        readonly get => _orientation;
        set => _orientation = NormalizeOrientation(value);
    }

    public void Relocate(float newX, float newY, float newZ)
    {
        X = newX;
        Y = newY;
        Z = newZ;
    }

    public void Relocate(float newX, float newY, float newZ, float newOrientation)
    {
        Relocate(newX, newY, newZ);
        Orientation = newOrientation;
    }

    public void Relocate(Position position) => Relocate(position.X, position.Y, position.Z, position.Orientation);

    /// <summary>
    /// Moves by an offset expressed in this position's own frame of reference.
    /// </summary>
    /// <remarks>
    /// The <c>sin(o + π)</c> in the X term is not a typo — it is what upstream computes, and vehicle
    /// and transport passenger offsets are tuned against it. Simplifying it to <c>-sin(o)</c> is
    /// algebraically identical but changes the float rounding, so leave it alone.
    /// </remarks>
    public void RelocateOffset(Position offset)
    {
        float cos = MathF.Cos(Orientation);

        X += (offset.X * cos) + (offset.Y * MathF.Sin(Orientation + MathF.PI));
        Y += (offset.Y * cos) + (offset.X * MathF.Sin(Orientation));
        Z += offset.Z;
        Orientation += offset.Orientation;
    }

    /// <summary>Expresses <paramref name="target"/> as an offset in this position's frame.</summary>
    public readonly Position GetPositionOffsetTo(Position target)
    {
        float dx = target.X - X;
        float dy = target.Y - Y;
        float cos = MathF.Cos(Orientation);
        float sin = MathF.Sin(Orientation);

        return new Position(
            (dx * cos) + (dy * sin),
            (dy * cos) - (dx * sin),
            target.Z - Z,
            target.Orientation - Orientation);
    }

    public readonly float GetExactDist2dSq(float otherX, float otherY)
    {
        float dx = otherX - X;
        float dy = otherY - Y;
        return (dx * dx) + (dy * dy);
    }

    public readonly float GetExactDist2dSq(Position other) => GetExactDist2dSq(other.X, other.Y);

    public readonly float GetExactDist2d(float otherX, float otherY) => MathF.Sqrt(GetExactDist2dSq(otherX, otherY));

    public readonly float GetExactDist2d(Position other) => GetExactDist2d(other.X, other.Y);

    public readonly float GetExactDistSq(float otherX, float otherY, float otherZ)
    {
        float dz = otherZ - Z;
        return GetExactDist2dSq(otherX, otherY) + (dz * dz);
    }

    public readonly float GetExactDistSq(Position other) => GetExactDistSq(other.X, other.Y, other.Z);

    public readonly float GetExactDist(float otherX, float otherY, float otherZ) =>
        MathF.Sqrt(GetExactDistSq(otherX, otherY, otherZ));

    public readonly float GetExactDist(Position other) => GetExactDist(other.X, other.Y, other.Z);

    /// <summary>Absolute angle from here to a point, in <c>[0, 2π)</c>.</summary>
    public readonly float GetAngle(float otherX, float otherY) =>
        NormalizeOrientation(MathF.Atan2(otherY - Y, otherX - X));

    public readonly float GetAngle(Position other) => GetAngle(other.X, other.Y);

    /// <summary>Angle to a point relative to where this position is facing.</summary>
    public readonly float GetRelativeAngle(float otherX, float otherY) =>
        NormalizeOrientation(GetAngle(otherX, otherY) - Orientation);

    public readonly float GetRelativeAngle(Position other) => GetRelativeAngle(other.X, other.Y);

    public readonly float ToAbsoluteAngle(float relativeAngle) => NormalizeOrientation(relativeAngle + Orientation);

    /// <summary>Strictly-less-than, matching upstream: a point exactly at <c>dist</c> is not in range.</summary>
    public readonly bool IsInDist2d(float otherX, float otherY, float distance) =>
        GetExactDist2dSq(otherX, otherY) < distance * distance;

    public readonly bool IsInDist2d(Position other, float distance) => IsInDist2d(other.X, other.Y, distance);

    public readonly bool IsInDist(float otherX, float otherY, float otherZ, float distance) =>
        GetExactDistSq(otherX, otherY, otherZ) < distance * distance;

    public readonly bool IsInDist(Position other, float distance) => IsInDist(other.X, other.Y, other.Z, distance);

    /// <summary>Whether a point falls inside an arc of <paramref name="arc"/> radians centred on the facing.</summary>
    public readonly bool HasInArc(float arc, Position other, float targetRadius = 0f)
    {
        float normalizedArc = NormalizeOrientation(arc);

        float angle = NormalizeOrientation(GetAngle(other) - Orientation);
        if (angle > MathF.PI)
        {
            angle -= 2f * MathF.PI;
        }

        float halfArc = normalizedArc / 2f;
        float lowerBorder = -halfArc;
        float upperBorder = halfArc;

        if (targetRadius > 0f)
        {
            float distance = GetExactDist2d(other);
            if (distance > targetRadius)
            {
                float widening = MathF.Asin(targetRadius / distance);
                lowerBorder -= widening;
                upperBorder += widening;
            }
            else
            {
                // Standing inside the target's radius: every angle is in arc.
                return true;
            }
        }

        return angle >= lowerBorder && angle <= upperBorder;
    }

    /// <summary>
    /// Wraps an angle into <c>[0, 2π)</c>.
    /// </summary>
    /// <remarks>
    /// Negative inputs are folded by mirroring rather than by a single <c>fmod</c>, because C's
    /// <c>fmod</c> keeps the sign of its argument. This is upstream's exact sequence; a "cleaner"
    /// version that differs in the last float bit will make orientation comparisons disagree with
    /// the C++ server.
    /// </remarks>
    public static float NormalizeOrientation(float orientation)
    {
        const float TwoPi = 2f * MathF.PI;

        if (orientation < 0)
        {
            // Note that an exact negative multiple of 2π comes back as 2π, not 0. Upstream has the
            // same edge and callers compare against it, so it is preserved rather than fixed.
            float folded = -orientation % TwoPi;
            return -folded + TwoPi;
        }

        return orientation % TwoPi;
    }

    public readonly bool Equals(Position other) =>
        X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z) && Orientation.Equals(other.Orientation);

    public override readonly bool Equals(object? obj) => obj is Position other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Z, Orientation);

    public static bool operator ==(Position left, Position right) => left.Equals(right);

    public static bool operator !=(Position left, Position right) => !left.Equals(right);

    public override readonly string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"X: {X:F3} Y: {Y:F3} Z: {Z:F3} O: {Orientation:F3}");
}

/// <summary>
/// A <see cref="Position"/> that also knows which map it is on.
/// </summary>
/// <remarks>
/// A class, not a struct, and deliberately so: <see cref="InvalidMapId"/> has to be the default.
/// A struct's <c>default</c> is all zeroes, and map 0 is Eastern Kingdoms — a real, populated map —
/// so a default-constructed struct would silently mean "somewhere in Azeroth" instead of "nowhere".
/// <see cref="Position"/> stays a struct because it is the one that gets copied per tick; world
/// locations are teleport targets and spawn points, not hot-path data.
/// </remarks>
public sealed class WorldLocation : IEquatable<WorldLocation>
{
    /// <summary>"No map", as upstream spells it.</summary>
    public const uint InvalidMapId = 0xFFFFFFFF;

    public WorldLocation(
        uint mapId = InvalidMapId,
        float x = 0f,
        float y = 0f,
        float z = 0f,
        float orientation = 0f)
    {
        MapId = mapId;
        Position = new Position(x, y, z, orientation);
    }

    public WorldLocation(uint mapId, Position position)
    {
        MapId = mapId;
        Position = position;
    }

    public uint MapId { get; set; }

    public Position Position { get; set; }

    /// <summary>Whether this location points at an actual map.</summary>
    public bool IsValid => MapId != InvalidMapId;

    public void WorldRelocate(uint newMapId, Position position)
    {
        MapId = newMapId;
        Position = position;
    }

    public bool Equals(WorldLocation? other) =>
        other is not null && MapId == other.MapId && Position.Equals(other.Position);

    public override bool Equals(object? obj) => Equals(obj as WorldLocation);

    public override int GetHashCode() => HashCode.Combine(MapId, Position);

    public static bool operator ==(WorldLocation? left, WorldLocation? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(WorldLocation? left, WorldLocation? right) => !(left == right);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Map: {MapId} {Position}");
}
