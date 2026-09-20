using Mnemosyne.Protocol;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mnemosyne.Client;

// Client for the Mnemosyne named-pipe service (spec: Ariadne docs/mnemosyne-protocol.md).
// Calls are serialized internally; create multiple clients for parallelism.
public sealed class MnemosyneClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _mutex = new SemaphoreSlim(1, 1);
    private int _nextId;

    public bool Connected => _pipe?.IsConnected ?? false;

    public async Task ConnectAsync(int timeoutMs = 3000, CancellationToken cancel = default)
    {
        var pipe = new NamedPipeClientStream(".", MnemosynePipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, cancel).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
    }

    public async Task<T> SendAsync<T>(Request request, CancellationToken cancel = default) where T : Response
    {
        var reader = _reader ?? throw new InvalidOperationException("not connected");
        var writer = _writer!;
        request.Id = Interlocked.Increment(ref _nextId);
        await _mutex.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, MnemosynePipe.JsonOptions)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
                throw new IOException("pipe closed by server");
            var response = JsonSerializer.Deserialize<T>(line, MnemosynePipe.JsonOptions)
                ?? throw new IOException("malformed response");
            if (response.Id != request.Id)
                throw new IOException($"response id mismatch (sent {request.Id}, got {response.Id})");
            return response;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public Task<HelloResponse> HelloAsync(CancellationToken cancel = default) =>
        SendAsync<HelloResponse>(new Request { Op = "hello" }, cancel);

    public Task<ListZonesResponse> ListZonesAsync(CancellationToken cancel = default) =>
        SendAsync<ListZonesResponse>(new Request { Op = "listZones" }, cancel);

    public Task<ZoneStatusResponse> ZoneStatusAsync(string cacheKey, CancellationToken cancel = default) =>
        SendAsync<ZoneStatusResponse>(new Request { Op = "zoneStatus", CacheKey = cacheKey }, cancel);

    public Task<GetMeshResponse> GetMeshAsync(string cacheKey, CancellationToken cancel = default) =>
        SendAsync<GetMeshResponse>(new Request { Op = "getMesh", CacheKey = cacheKey }, cancel);

    public Task<FindPathResponse> FindPathAsync(string cacheKey, float[] from, float[] to, bool fly = false, CancellationToken cancel = default) =>
        SendAsync<FindPathResponse>(new Request { Op = "findPath", CacheKey = cacheKey, From = from, To = to, Fly = fly }, cancel);

    public Task<Response> NotifyMeshBuiltAsync(string cacheKey, string path, CancellationToken cancel = default) =>
        SendAsync<Response>(new Request { Op = "notifyMeshBuilt", CacheKey = cacheKey, Path = path }, cancel);

    public Task<Response> UpdateGameStateAsync(string cacheKey, uint territoryId, float[] pos, float rotation, bool flying,
        string? character = null, CancellationToken cancel = default) =>
        SendAsync<Response>(new Request { Op = "updateGameState", CacheKey = cacheKey, TerritoryId = territoryId,
            Pos = pos, Rotation = rotation, Flying = flying, Character = character }, cancel);

    public Task<GameStateResponse> GetGameStateAsync(CancellationToken cancel = default) =>
        SendAsync<GameStateResponse>(new Request { Op = "getGameState" }, cancel);

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _mutex.Dispose();
    }
}
