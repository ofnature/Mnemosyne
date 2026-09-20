using Navmesh.NavVolume;
using System.Numerics;

namespace Mnemosyne.Viewer;

// Translucent amber boxes for the solid (non-flyable) voxels of a VoxelMap.
// Rendered down to level 1 (~8m cells); deeper partially-solid cells draw as full boxes,
// which keeps the mesh size sane at the cost of a slightly thick shell.
public sealed class VolumeGeometry
{
    public float[] Positions = [];
    public byte[] Colors = [];
    public int Boxes;

    private const int MaxRenderLevel = 1;
    private const byte BaseR = 245, BaseG = 150, BaseB = 50, Alpha = 80;

    public static VolumeGeometry Build(VoxelMap volume)
    {
        var pos = new List<float>(1 << 18);
        var col = new List<byte>(1 << 18);
        int boxes = 0;
        AddTile(volume, volume.RootTile, pos, col, ref boxes);
        return new() { Positions = [.. pos], Colors = [.. col], Boxes = boxes };
    }

    private static void AddTile(VoxelMap volume, VoxelMap.Tile tile, List<float> pos, List<byte> col, ref int boxes)
    {
        var ld = tile.LevelDesc;
        for (int i = 0; i < tile.Contents.Length; ++i)
        {
            var v = tile.Contents[i];
            if ((v & VoxelMap.VoxelOccupiedBit) == 0)
                continue;
            var id = (ushort)(v & VoxelMap.VoxelIdMask);
            if (id != VoxelMap.VoxelIdMask && tile.Level < MaxRenderLevel)
            {
                AddTile(volume, tile.Subdivision[id], pos, col, ref boxes);
                continue;
            }
            // solid leaf, or partially-solid cell below render depth
            var (min, max) = tile.CalculateSubdivisionBounds(ld.IndexToVoxel((ushort)i));
            AddBox(volume, pos, col, min, max);
            ++boxes;
        }
    }

    // solid at render granularity (partially-solid render-level cells count as solid)
    private static bool SolidAt(VoxelMap volume, Vector3 p)
    {
        var tile = volume.RootTile;
        while (true)
        {
            var v = tile.WorldToVoxel(p);
            if (!tile.LevelDesc.InBounds(v))
                return false;
            var data = tile.Contents[tile.LevelDesc.VoxelToIndex(v)];
            if ((data & VoxelMap.VoxelOccupiedBit) == 0)
                return false;
            var id = (ushort)(data & VoxelMap.VoxelIdMask);
            if (id == VoxelMap.VoxelIdMask || tile.Level >= MaxRenderLevel)
                return true;
            tile = tile.Subdivision[id];
        }
    }

    private static void AddBox(VoxelMap volume, List<float> pos, List<byte> col, Vector3 min, Vector3 max)
    {
        Span<Vector3> c =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z),
            new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z),
            new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z),
            new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        var center = (min + max) * 0.5f;
        var size = max - min;
        // emit only faces whose neighbor cell is not solid
        if (!SolidAt(volume, center + new Vector3(0, size.Y, 0)))
            AddQuad(pos, col, c[4], c[5], c[6], c[7], 1.0f);    // top
        if (!SolidAt(volume, center - new Vector3(0, size.Y, 0)))
            AddQuad(pos, col, c[3], c[2], c[1], c[0], 0.45f);   // bottom
        if (!SolidAt(volume, center - new Vector3(0, 0, size.Z)))
            AddQuad(pos, col, c[0], c[1], c[5], c[4], 0.7f);    // -Z
        if (!SolidAt(volume, center + new Vector3(0, 0, size.Z)))
            AddQuad(pos, col, c[2], c[3], c[7], c[6], 0.7f);    // +Z
        if (!SolidAt(volume, center + new Vector3(size.X, 0, 0)))
            AddQuad(pos, col, c[1], c[2], c[6], c[5], 0.55f);   // +X
        if (!SolidAt(volume, center - new Vector3(size.X, 0, 0)))
            AddQuad(pos, col, c[3], c[0], c[4], c[7], 0.55f);   // -X
    }

    private static void AddQuad(List<float> pos, List<byte> col, Vector3 a, Vector3 b, Vector3 c, Vector3 d, float shade)
    {
        byte r = (byte)(BaseR * shade), g = (byte)(BaseG * shade), bl = (byte)(BaseB * shade);
        foreach (var v in (ReadOnlySpan<Vector3>)[a, b, c, a, c, d])
        {
            pos.Add(v.X); pos.Add(v.Y); pos.Add(v.Z);
            col.Add(r); col.Add(g); col.Add(bl); col.Add(Alpha);
        }
    }
}
