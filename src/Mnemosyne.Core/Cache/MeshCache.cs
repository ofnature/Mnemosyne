using System.IO;

namespace Mnemosyne.Core;

public sealed record MeshCacheEntry(string Path, string Key, uint Version, int CustomizationVersion)
{
    public bool IsSupported => Version == global::Navmesh.Navmesh.Version;
}

// Read-only index over vnavmesh's meshcache directory.
public static class MeshCache
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "XIVLauncher", "pluginConfigs", "vnavmesh", "meshcache");

    public static IEnumerable<MeshCacheEntry> Enumerate(string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory))
            yield break;

        foreach (var path in Directory.EnumerateFiles(directory, "*.navmesh"))
        {
            MeshCacheEntry? entry = null;
            try
            {
                var (magic, version, customization) = ReadHeader(path);
                if (magic == global::Navmesh.Navmesh.Magic)
                    entry = new(path, Path.GetFileNameWithoutExtension(path), version, customization);
            }
            catch (IOException)
            {
                // file locked (vnavmesh mid-write) or truncated - skip
            }
            if (entry != null)
                yield return entry;
        }
    }

    // Zone entry is peak file contention (see Ariadne's mnemosyne-protocol.md): vnavmesh may
    // be rewriting the file while we read and Ariadne validates. So: never take a handle
    // that blocks writers (ReadWrite|Delete share), keep the open window as short as
    // possible (read bytes, close, parse from memory), and retry transient IO errors.
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public static T RetryIO<T>(Func<T> operation, int attempts = 3, int delayMs = 150)
    {
        for (int i = 0; ; ++i)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    public static byte[] ReadAllBytesShared(string path) => RetryIO(() =>
    {
        using var stream = OpenShared(path);
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    });

    public static (uint Magic, uint Version, int CustomizationVersion) ReadHeader(string path) => RetryIO(() =>
    {
        using var stream = OpenShared(path);
        using var reader = new BinaryReader(stream);
        return (reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt32());
    });

    // Loads a cache file, accepting whatever customization version it was built with
    // (a mismatch is vnavmesh's rebuild trigger, not a corruption indicator for us).
    public static global::Navmesh.Navmesh Load(string path)
    {
        var bytes = ReadAllBytesShared(path); // file handle released before the slow parse
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var customization = bytes.Length >= 12 ? BitConverter.ToInt32(bytes, 8) : 0;
        return global::Navmesh.Navmesh.Deserialize(reader, customization);
    }
}
