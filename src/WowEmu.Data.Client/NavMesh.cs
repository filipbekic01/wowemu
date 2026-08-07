using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace WowEmu.Data.Client;

/// <summary>
/// Turns AzerothCore's navigation tiles into something Detour can path over.
/// </summary>
/// <remarks>
/// <para>
/// PLAN.md §3.4.1 records this as a decision that had to be taken deliberately: AzerothCore vendors
/// a <i>patched</i> Recast &amp; Detour — <c>DT_POLYREF64</c>, and a 12/21/31 salt/tile/poly split
/// against stock's runtime-derived one — and the fear was that a stock port would misread every tile
/// into plausible garbage. The conclusion was that we would have to fork and maintain a third-party
/// codebase.
/// </para>
/// <para>
/// <b>That turned out not to be so, and the reason is worth recording.</b> The patched constants
/// affect two things: the width of a <c>dtPolyRef</c>, and how one is packed. Only the first reaches
/// the disk, through <c>sizeof(dtLink)</c> — and every shipped tile measures as 64-bit, which
/// DotRecast is too (<c>DtLink.refs</c> is a <c>long</c>). The <i>packing</i> never reaches the
/// disk at all: <see cref="DtNavMesh.AddTile"/> takes a parsed <see cref="DtMeshData"/> rather than
/// a byte blob, and <see cref="DtMeshData"/> has no link array — Detour rebuilds every link itself
/// when a tile is added. A polygon reference is therefore created and consumed entirely within one
/// process and is never compared against anything AzerothCore wrote.
/// </para>
/// <para>
/// So the tile format is ours to read — which <see cref="NavMeshFile"/> already did — and the
/// library is a stock package rather than a fork.
/// </para>
/// </remarks>
public static class NavMesh
{
    /// <summary>Vertex indices per polygon. <c>DT_VERTS_PER_POLYGON</c>.</summary>
    public const int VertsPerPolygon = 6;

    /// <summary>
    /// Builds an empty mesh from a map's <c>.mmap</c> parameters.
    /// </summary>
    /// <remarks>
    /// <b><c>maxPolys</c> is legitimately negative in the file.</b> The generator writes
    /// <c>1 &lt;&lt; 31</c> into a signed <c>int</c>, so it arrives as <c>-2147483648</c>. Detour
    /// only ever uses it to size a bit field, and passing the negative value through is what keeps
    /// that arithmetic matching the generator's.
    /// </remarks>
    public static DtNavMesh Create(NavMeshParams parameters)
    {
        DtNavMeshParams option = new()
        {
            orig = new RcVec3f(parameters.OriginX, parameters.OriginY, parameters.OriginZ),
            tileWidth = parameters.TileWidth,
            tileHeight = parameters.TileHeight,
            maxTiles = parameters.MaxTiles,
            maxPolys = parameters.MaxPolys,
        };

        DtNavMesh mesh = new();
        mesh.Init(option, VertsPerPolygon);

        return mesh;
    }

    /// <summary>
    /// Converts one parsed tile into Detour's own shape.
    /// </summary>
    /// <remarks>
    /// A transcription, field for field — the two structures describe the same thing because both
    /// are the same C original. What is <i>not</i> carried across is the link array: Detour builds
    /// links when the tile is added, and the ones in the file are allocated space rather than
    /// meaningful data.
    /// </remarks>
    public static DtMeshData ToMeshData(DetourTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);

        DetourMeshHeader source = tile.Header;

        DtMeshData data = new()
        {
            header = new DtMeshHeader
            {
                magic = source.Magic,
                version = source.Version,
                x = source.X,
                y = source.Y,
                layer = source.Layer,
                userId = (int)source.UserId,
                polyCount = source.PolyCount,
                vertCount = source.VertCount,
                maxLinkCount = source.MaxLinkCount,
                detailMeshCount = source.DetailMeshCount,
                detailVertCount = source.DetailVertCount,
                detailTriCount = source.DetailTriCount,
                bvNodeCount = source.BvNodeCount,
                offMeshConCount = source.OffMeshConCount,
                offMeshBase = source.OffMeshBase,
                walkableHeight = source.WalkableHeight,
                walkableRadius = source.WalkableRadius,
                walkableClimb = source.WalkableClimb,
                bmin = new RcVec3f(source.BoundsMinX, source.BoundsMinY, source.BoundsMinZ),
                bmax = new RcVec3f(source.BoundsMaxX, source.BoundsMaxY, source.BoundsMaxZ),
                bvQuantFactor = source.BvQuantFactor,
            },
            verts = tile.Vertices,
            detailVerts = tile.DetailVertices,
            polys = new DtPoly[tile.Polys.Length],
            detailMeshes = new DtPolyDetail[tile.DetailMeshes.Length],
            detailTris = new int[tile.DetailTriangles.Length],
            bvTree = new DtBVNode[tile.BvTree.Length],
            offMeshCons = new DtOffMeshConnection[tile.OffMeshConnections.Length],
        };

        for (int i = 0; i < tile.Polys.Length; i++)
        {
            DetourPoly poly = tile.Polys[i];
            DtPoly converted = new(i, VertsPerPolygon)
            {
                flags = poly.Flags,
                vertCount = poly.VertCount,

                // Area and type stay packed. Detour unpacks them itself through GetArea/GetPolyType,
                // and splitting them here would leave both halves in the wrong place.
                areaAndtype = poly.AreaAndType,
            };

            for (int v = 0; v < VertsPerPolygon; v++)
            {
                converted.verts[v] = poly.Verts[v];
                converted.neis[v] = poly.Neighbours[v];
            }

            data.polys[i] = converted;
        }

        for (int i = 0; i < tile.DetailMeshes.Length; i++)
        {
            DetourPolyDetail detail = tile.DetailMeshes[i];

            data.detailMeshes[i] = new DtPolyDetail(
                (int)detail.VertBase, (int)detail.TriBase, detail.VertCount, detail.TriCount);
        }

        // Widened from bytes: the file stores a detail triangle as four bytes and Detour wants four
        // ints. Copying the bytes across as-is would pack four triangles into one.
        for (int i = 0; i < tile.DetailTriangles.Length; i++)
        {
            data.detailTris[i] = tile.DetailTriangles[i];
        }

        for (int i = 0; i < tile.BvTree.Length; i++)
        {
            DetourBvNode node = tile.BvTree[i];

            data.bvTree[i] = new DtBVNode
            {
                bmin = new RcVec3i(node.BoundsMin[0], node.BoundsMin[1], node.BoundsMin[2]),
                bmax = new RcVec3i(node.BoundsMax[0], node.BoundsMax[1], node.BoundsMax[2]),
                i = node.Index,
            };
        }

        for (int i = 0; i < tile.OffMeshConnections.Length; i++)
        {
            DetourOffMeshConnection connection = tile.OffMeshConnections[i];

            data.offMeshCons[i] = new DtOffMeshConnection
            {
                pos =
                [
                    new RcVec3f(connection.StartX, connection.StartY, connection.StartZ),
                    new RcVec3f(connection.EndX, connection.EndY, connection.EndZ),
                ],
                rad = connection.Radius,
                poly = connection.Poly,
                flags = connection.Flags,
                side = connection.Side,
                userId = (int)connection.UserId,
            };
        }

        return data;
    }
}
