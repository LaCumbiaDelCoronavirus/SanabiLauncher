using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Inter-process communication manager. This is
/// </summary>
public static class IpcManager
{
    public const string SanabiIpcName = "sanabiss14launcheripc";
    public const string SanabiCrashIpcName = "sanabiss14launchercrashipc";

    /// <summary>
    ///     Connects and starts running the server pipe, writing <paramref name="value"/> as a UTF-8 JSON payload.
    ///         Unlike a raw struct copy this handles variable-length data (strings, lists, nested objects), so the
    ///         payload type need not be a blittable struct. The server waits until the client has drained the
    ///         payload, then disconnects.
    /// </summary>
    public static async Task RunObjectPipeServer<TDatum>(string pipeName, TDatum value, JsonTypeInfo<TDatum> typeInfo)
    {
        var server = InitiateServer(pipeName, pipeDirection: PipeDirection.Out);
        await server.WaitForConnectionAsync();

        var data = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        await server.WriteAsync(data);
        await server.FlushAsync();
        server.WaitForPipeDrain();

        server.Disconnect();
        server.Dispose();
    }

    /// <summary>
    ///     Connects and waits for a single client to connect and send a complete text payload,
    ///         then disconnects. Intended for one-shot, best-effort notifications (e.g. crash
    ///         reports) where the client may never connect during the lifetime of this call;
    ///         pass <paramref name="cancel"/> to give up waiting once it's no longer needed.
    /// </summary>
    /// <returns>The received text, or <c>null</c> if cancelled before a client connected.</returns>
    public static async Task<string?> RunStringPipeServer(string pipeName, CancellationToken cancel = default)
    {
        // Allow a couple of overlapping instances: cancelling a listener's connection wait has
        // real OS-level latency, so a rapid relaunch may briefly race a previous listener that
        // hasn't finished tearing down yet.
        var server = InitiateServer(pipeName, pipeDirection: PipeDirection.In, maxInstances: 4);

        try
        {
            await server.WaitForConnectionAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            server.Dispose();
            return null;
        }

        var reader = new StreamReader(server);
        var message = await reader.ReadToEndAsync(cancel);

        server.Disconnect();
        server.Dispose();
        return message;
    }

    /// <summary>
    ///     Connects to the server pipe and sends a single text payload, then disconnects. This is
    ///         synchronous and gives up after <paramref name="timeoutMs"/> milliseconds if no
    ///         server is listening, so it is safe to call even if the other end isn't running.
    /// </summary>
    public static void RunStringPipeClient(string pipeName, string message, int timeoutMs = 3000)
    {
        var client = InitiateClient(pipeName, pipeDirection: PipeDirection.Out);
        client.Connect(timeoutMs);

        var writer = new StreamWriter(client);
        writer.Write(message);
        writer.Flush();

        client.Dispose();
    }

    /// <summary>
    ///     Connects to the server pipe and reads the complete JSON payload written by
    ///         <see cref="RunObjectPipeServer{TDatum}"/>, deserializing it to <typeparamref name="TDatum"/>.
    ///         Synchronous; it is assumed that the server is already running.
    /// </summary>
    public static TDatum RunObjectPipeClient<TDatum>(string pipeName, JsonTypeInfo<TDatum> typeInfo)
    {
        var client = InitiateClient(pipeName, pipeDirection: PipeDirection.In);
        client.Connect();

        using var buffer = new MemoryStream();
        client.CopyTo(buffer); // reads until the server finishes writing and disconnects
        client.Dispose();

        return JsonSerializer.Deserialize(buffer.ToArray(), typeInfo)
            ?? throw new InvalidOperationException($"Received a null/empty {typeof(TDatum).Name} payload over pipe '{pipeName}'.");
    }

    /// <summary>
    ///     Connects and starts running the server pipe.
    /// </summary>
    /// <param name="sendAction">When called and the pipe is connected, writes the string directly to the pipe.</param>
    /// <param name="onLineReceived">Invoked with the read line every time a line is read from the pipe, from the server.</param>
    public static async Task<NamedPipeServerStream> RunPipeServer(string pipeName, Action<string> sendAction, Action<string>? onLineReceived = null)
    {
        var server = InitiateServer(pipeName);
        await server.WaitForConnectionAsync();
        InitialiseStreams(server, out var serverReader, out var serverWriter);

        sendAction = line => _ = serverWriter.WriteLineAsync(line);
        _ = Task.Run(async () => StartListening(serverReader, sendAction, onLineReceived));

        return server;
    }

    /// <summary>
    ///     Connects and starts running the client pipe.
    /// </summary>
    /// <param name="sendAction">When called and the pipe is connected, writes the string directly to the pipe.</param>
    /// <param name="onLineReceived">Invoked with the read line every time a line is read from the pipe, from the server.</param>
    public static async Task<NamedPipeClientStream> RunPipeClient(string pipeName, Action<string> sendAction, Action<string>? onLineReceived = null)
    {
        var client = InitiateClient(pipeName);
        await client.ConnectAsync();
        InitialiseStreams(client, out var clientReader, out var clientWriter);

        sendAction = line => _ = clientWriter.WriteLineAsync(line);
        _ = Task.Run(async () => StartListening(clientReader, sendAction, onLineReceived));

        return client;
    }

    private static async Task StartListening(StreamReader pipeReader, Action<string> sendAction, Action<string>? onLineReceived)
    {
        while (true)
        {
            string? line = await pipeReader.ReadLineAsync();
            if (line == null) break; // other side disconnected

            onLineReceived?.Invoke(line);
        }

        // Pipe closed, disable the action
        sendAction = _ => { };
    }

    /// <summary>
    ///     Creates a <see cref="NamedPipeServerStream"/>.
    /// </summary>
    private static NamedPipeServerStream InitiateServer(string pipeName, PipeDirection pipeDirection = PipeDirection.InOut, int maxInstances = 1)
        => new(pipeName, pipeDirection, maxInstances, PipeTransmissionMode.Byte);

    /// <summary>
    ///     Creates a <see cref="NamedPipeClientStream"/>.
    /// </summary>
    private static NamedPipeClientStream InitiateClient(string pipeName, PipeDirection pipeDirection = PipeDirection.InOut)
        => new(".", pipeName, pipeDirection);


    /// <summary>
    ///     Adds streams to a pipe. It must be connected first.
    /// </summary>
    public static void InitialiseStreams(PipeStream pipeStream, out StreamReader streamReader, out StreamWriter streamWriter)
    {
        streamReader = new StreamReader(pipeStream);
        streamWriter = new StreamWriter(pipeStream) { AutoFlush = true };
    }
}
