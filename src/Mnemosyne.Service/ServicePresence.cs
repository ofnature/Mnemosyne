namespace Mnemosyne.Service;

// Ariadne autostarts us when the pipe is missing, which means it needs to know where our
// exe lives without hardcoding a build directory, and the operator needs our output even
// though an autostarted service has no console window. Both are one file in the data dir.
public static class ServicePresence
{
    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne");

    /// <summary>Record our exe path so Ariadne can start us next time. Written on every
    /// run, so moving or rebuilding the service just works.</summary>
    public static void StampExePath()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "service.path"), exe);
        }
        catch (IOException)
        {
            // another instance stamping the same file; harmless, the content is identical
        }
    }

    /// <summary>Mirror console output to a log file. Returns a writer to keep alive for the
    /// process lifetime.</summary>
    public static IDisposable TeeConsoleToLog()
    {
        Directory.CreateDirectory(DataDir);
        var log = new StreamWriter(new FileStream(Path.Combine(DataDir, "service.log"),
            FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Console.SetOut(new TeeWriter(Console.Out, log));
        return log;
    }

    private sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
    {
        public override System.Text.Encoding Encoding => a.Encoding;

        public override void Write(char value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void Write(string? value)
        {
            a.Write(value);
            b.Write(value);
        }

        public override void WriteLine(string? value)
        {
            a.WriteLine(value);
            b.WriteLine(value);
        }
    }
}
