using Mnemosyne.Client;
using System.Numerics;

namespace Mnemosyne.Viewer;

// Background link to the Mnemosyne service: polls getGameState ~10 Hz and exposes the
// latest snapshot to the frame loop. Reconnects with backoff if the service is away.
public sealed class GameLink : IDisposable
{
    public sealed record Snapshot(string? CacheKey, Vector3 Pos, float Rotation, bool Flying, double AgeMs,
        float Speed, float? GroundSpeed, float? FlySpeed);

    /// <summary>One toon in the fleet. The user runs several game clients at once, so the
    /// viewer has to show all of them, not just whichever pushed last.</summary>
    public sealed record Player(int ClientId, string? Character, string? CacheKey, Vector3 Pos, float Rotation,
        bool Flying, float Speed, double AgeMs);

    private readonly CancellationTokenSource _cts = new();
    private volatile Snapshot? _latest;
    private volatile bool _connected;

    public Snapshot? Latest => _latest;
    public bool Connected => _connected;

    private volatile IReadOnlyList<Player> _players = [];

    public IReadOnlyList<Player> Players => _players;

    // sticky: the service reports calibration even while logged out, and consumers
    // (ETA, path animation) need it whether or not a player is currently loaded
    public float? GroundSpeed { get; private set; }
    public float? FlySpeed { get; private set; }

    public GameLink()
    {
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            MnemosyneClient? client = null;
            try
            {
                client = new MnemosyneClient();
                await client.ConnectAsync(1000, _cts.Token);
                var hello = await client.HelloAsync(_cts.Token);
                if (hello.App != "mnemosyne")
                    throw new IOException($"connected to '{hello.App}', not the service");
                _connected = true;

                while (!_cts.IsCancellationRequested)
                {
                    var state = await client.GetGameStateAsync(_cts.Token);
                    if (state.Speeds?.Ground is { } g)
                        GroundSpeed = g;
                    if (state.Speeds?.Fly is { } f)
                        FlySpeed = f;
                    _players = state is { Ok: true, Players: { } list }
                        ? [.. list.Where(p => p.Pos is { Length: 3 }).Select(p => new Player(p.ClientId, p.Character,
                            p.CacheKey, new Vector3(p.Pos![0], p.Pos[1], p.Pos[2]), p.Rotation, p.Flying, p.Speed, p.AgeMs))]
                        : [];
                    _latest = state is { Ok: true, Present: true, Pos.Length: 3 }
                        ? new Snapshot(state.CacheKey, new Vector3(state.Pos[0], state.Pos[1], state.Pos[2]), state.Rotation, state.Flying, state.AgeMs,
                            state.Speed, state.Speeds?.Ground, state.Speeds?.Fly)
                        : null;
                    await Task.Delay(100, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // service missing or connection dropped - back off and retry
                _connected = false;
                _latest = null;
                _players = [];
                try
                {
                    await Task.Delay(3000, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _connected = false;
    }
}
