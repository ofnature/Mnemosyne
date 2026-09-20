namespace Mnemosyne.Service;

/// <summary>Which build of the service is answering: the exe this process started from, and that
/// file's write time. A staged service can run for a week while the tree moves on and be invisible
/// from the outside — this machine's pipe answered 2026-09-12 behaviour for a week while every
/// in-process check passed. So it travels on `hello` and goes into the start-up log.
///
/// It lives here rather than in the Protocol project on purpose: that project is netstandard2.1 for
/// the plugins' sake and has no <c>Environment.ProcessPath</c>. The wire *fields* are the
/// Protocol's; knowing which build this process is, is the service's business.</summary>
public static class ServiceBuild
{
    public static string Exe => Environment.ProcessPath ?? "(unknown)";

    public static string BuiltAt => File.Exists(Exe)
        ? File.GetLastWriteTimeUtc(Exe).ToString("yyyy-MM-dd HH:mm:ss'Z'")
        : "(unknown)";
}
