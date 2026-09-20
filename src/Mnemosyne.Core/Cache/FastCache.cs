using DotRecast.Detour;
using Navmesh.NavVolume;

namespace Mnemosyne.Core;

// Second-level cache: vnavmesh's .navmesh files are Brotli-compressed and decode slowly
// (~3s for a large zone). On first load we re-save as an uncompressed segmented .mnav in
// Mnemosyne's own cache dir; later loads read that directly, and the fly volume lives in
// its own segment so walk-only consumers never pay for it. Invalidated by source file
// length+mtime (and rebuilt transparently on any read failure).
public static class FastCache
{
    private const uint Magic = 0x56414E4D; // 'MNAV'
    private const uint Version = 1;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "cache");

    public sealed class Loaded
    {
        public required DtNavMesh Mesh;
        public required int CustomizationVersion;
        public VoxelMap? Volume;          // null if not requested or zone has none
        public required bool HasVolume;   // whether a volume segment exists to load later
        public required string FastPath;  // for LoadVolume upgrades
        public required bool FromFastCache;
    }

    public static Loaded Load(string sourcePath, bool withVolume)
    {
        var fastPath = FastPathFor(sourcePath);
        var src = new FileInfo(sourcePath);

        if (File.Exists(fastPath))
        {
            try
            {
                var loaded = ReadFast(fastPath, src, withVolume);
                if (loaded != null)
                    return loaded;
            }
            catch (Exception)
            {
                // corrupt/stale fast file - fall through and rebuild it
            }
        }

        // slow path: decode the vnavmesh file, then bake the fast copy
        var nav = MeshCache.Load(sourcePath);
        try
        {
            WriteFast(fastPath, src, nav);
        }
        catch (Exception)
        {
            // read-only disk etc. - fast cache is an optimization, never a requirement
        }
        return new()
        {
            Mesh = nav.Mesh,
            CustomizationVersion = nav.CustomizationVersion,
            Volume = nav.Volume, // slow path already decoded it; no reason to discard
            HasVolume = nav.Volume != null,
            FastPath = fastPath,
            FromFastCache = false,
        };
    }

    // load just the volume segment of an already-validated fast file (lazy fly upgrade)
    public static VoxelMap? LoadVolume(string fastPath)
    {
        byte[]? volumeBytes = MeshCache.RetryIO<byte[]?>(() =>
        {
            using var stream = MeshCache.OpenShared(fastPath);
            using var reader = new BinaryReader(stream);
            var header = ReadHeader(reader);
            if (header == null || header.Value.VolumeLength == 0)
                return null;
            return ReadSegment(stream, header.Value.VolumeOffset, header.Value.VolumeLength);
        });
        return volumeBytes == null ? null
            : global::Navmesh.Navmesh.DeserializeVolume(new BinaryReader(new MemoryStream(volumeBytes)));
    }

    private static byte[] ReadSegment(FileStream stream, long offset, long length)
    {
        stream.Position = offset;
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string FastPathFor(string sourcePath) =>
        Path.Combine(Directory, Path.GetFileNameWithoutExtension(sourcePath) + ".mnav");

    private readonly record struct Header(
        long SrcLength, long SrcMtimeTicks, uint SrcVersion, int Customization,
        long MeshOffset, long MeshLength, long VolumeOffset, long VolumeLength);

    private static Header? ReadHeader(BinaryReader reader)
    {
        if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
            return null;
        return new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadUInt32(), reader.ReadInt32(),
            reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
    }

    private static Loaded? ReadFast(string fastPath, FileInfo src, bool withVolume)
    {
        // read all needed bytes in one short shared-open window, parse after closing -
        // holding a handle across the (slow) parse blocks writers during zone entry
        Header header = default;
        byte[]? meshBytes = null;
        byte[]? volumeBytes = null;
        bool valid = MeshCache.RetryIO(() =>
        {
            using var stream = MeshCache.OpenShared(fastPath);
            using var reader = new BinaryReader(stream);
            var headerOrNull = ReadHeader(reader);
            if (headerOrNull is not { } h)
                return false;
            if (h.SrcLength != src.Length || h.SrcMtimeTicks != src.LastWriteTimeUtc.Ticks
                || h.SrcVersion != global::Navmesh.Navmesh.Version)
                return false; // source changed - stale
            header = h;
            meshBytes = ReadSegment(stream, h.MeshOffset, h.MeshLength);
            if (withVolume && h.VolumeLength > 0)
                volumeBytes = ReadSegment(stream, h.VolumeOffset, h.VolumeLength);
            return true;
        });
        if (!valid || meshBytes == null)
            return null;

        var mesh = global::Navmesh.Navmesh.DeserializeMesh(new BinaryReader(new MemoryStream(meshBytes)));
        var volume = volumeBytes != null
            ? global::Navmesh.Navmesh.DeserializeVolume(new BinaryReader(new MemoryStream(volumeBytes)))
            : null;

        return new()
        {
            Mesh = mesh,
            CustomizationVersion = header.Customization,
            Volume = volume,
            HasVolume = header.VolumeLength > 0,
            FastPath = fastPath,
            FromFastCache = true,
        };
    }

    // Same layout as the vendored Navmesh.SerializeMesh, but counts tiles by iterating:
    // DtNavMesh.GetTileCount() returns 0 for meshes that were themselves deserialized
    // (vnavmesh only ever serializes freshly built meshes, so it never hits this).
    private static void WriteMeshSegment(BinaryWriter writer, DtNavMesh mesh)
    {
        int tileCount = 0;
        for (int i = 0; i < mesh.GetMaxTiles(); ++i)
            if (mesh.GetTile(i)?.data?.header != null)
                ++tileCount;

        writer.Write(tileCount);
        global::Navmesh.Navmesh.SerializeMeshParams(writer, mesh.GetParams());
        writer.Write(mesh.GetMaxVertsPerPoly());
        for (int i = 0; i < mesh.GetMaxTiles(); ++i)
        {
            var tile = mesh.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            writer.Write(mesh.GetTileRef(tile));
            global::Navmesh.Navmesh.SerializeMeshTile(writer, tile.data);
        }
    }

    private static void WriteFast(string fastPath, FileInfo src, global::Navmesh.Navmesh nav)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = fastPath + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(src.Length);
            writer.Write(src.LastWriteTimeUtc.Ticks);
            writer.Write(global::Navmesh.Navmesh.Version);
            writer.Write(nav.CustomizationVersion);
            long tablePos = stream.Position;
            writer.Write(0L); writer.Write(0L); writer.Write(0L); writer.Write(0L); // placeholder

            long meshOffset = stream.Position;
            WriteMeshSegment(writer, nav.Mesh);
            long meshLength = stream.Position - meshOffset;

            long volumeOffset = 0, volumeLength = 0;
            if (nav.Volume != null)
            {
                volumeOffset = stream.Position;
                global::Navmesh.Navmesh.SerializeVolume(writer, nav.Volume);
                volumeLength = stream.Position - volumeOffset;
            }

            stream.Position = tablePos;
            writer.Write(meshOffset);
            writer.Write(meshLength);
            writer.Write(volumeOffset);
            writer.Write(volumeLength);
        }
        File.Move(temp, fastPath, true);
    }
}
