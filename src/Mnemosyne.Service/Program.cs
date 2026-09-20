using Mnemosyne.Core;
using Mnemosyne.Protocol;
using Mnemosyne.Service;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// One service serves every Ariadne on the machine (the user runs 4 game clients). A second
// launch must not race for the pipe, and Ariadne spawning the service on a missing pipe has
// to be safe from all four clients at once - so the guard is a machine-wide mutex, not a
// check-then-listen.
using var instanceGuard = new Mutex(true, @"Global\MnemosyneService", out bool isPrimary);
if (!isPrimary)
{
    Console.WriteLine("another Mnemosyne service is already running - exiting");
    return 0;
}

using var serviceLog = ServicePresence.TeeConsoleToLog();
ServicePresence.StampExePath();

var service = new ZoneService();
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Mnemosyne service v{ZoneService.AppVersion} listening on \\\\.\\pipe\\{MnemosynePipe.PipeName}");
Console.WriteLine($"Cache: {MeshCache.DefaultDirectory} ({MeshCache.Enumerate().Count(e => e.IsSupported)} current-version zones)");
Console.WriteLine($"build: {ServiceBuild.Exe} ({ServiceBuild.BuiltAt})");
Console.WriteLine("Ctrl+C to stop");

int nextClientId = 0;

while (!cts.IsCancellationRequested)
{
    var server = new NamedPipeServerStream(MnemosynePipe.PipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    try
    {
        await server.WaitForConnectionAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        server.Dispose();
        break;
    }
    // next listener goes up immediately; four clients starting together must not queue
    int clientId = Interlocked.Increment(ref nextClientId);
    _ = Task.Run(() => HandleClient(server, clientId));
}
Console.WriteLine("service stopped");
return 0;

async Task HandleClient(NamedPipeServerStream pipe, int clientId)
{
    Console.WriteLine($"client {clientId} connected");
    try
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\n", AutoFlush = true };
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            Response response;
            try
            {
                var request = JsonSerializer.Deserialize<Request>(line, MnemosynePipe.JsonOptions);
                response = request == null
                    ? new Response { Ok = false, Error = "empty request" }
                    : service.Handle(request, clientId);
            }
            catch (JsonException)
            {
                Console.WriteLine($"client {clientId}: malformed line, dropping");
                break; // per spec: malformed JSON drops the connection
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize<object>(response, MnemosynePipe.JsonOptions));
        }
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
    {
        // client went away or service shutting down
    }
    finally
    {
        pipe.Dispose();
        service.OnClientDisconnected(clientId);
        Console.WriteLine($"client {clientId} disconnected");
    }
}
