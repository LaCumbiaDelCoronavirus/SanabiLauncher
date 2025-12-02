using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sanabi.Framework.Game.Managers;

/// <summary>
///     Inter-process communication manager. This is
/// </summary>
public static class IpcManager
{
    public const string SanabiIpcName = "sanabiss14launcheripc";

    /// <summary>
    ///     Connects and starts running the server pipe. This directly moves an unmanaged structs
    ///         into the pipe. The server is disconnected when done.
    /// </summary>
    public static async Task RunStructPipeServer<TDatum>(string pipeName, TDatum transferredStruct) where TDatum : unmanaged
    {
        Console.WriteLine($"Starting server...");
        var server = InitiateServer(pipeName, transmissionMode: PipeTransmissionMode.Byte, pipeDirection: PipeDirection.Out);
        await server.WaitForConnectionAsync();

        Console.WriteLine($"Started server");

        var data = StructToMemory(ref transferredStruct);
        await server.WriteAsync(data);

        Console.WriteLine($"Wrote client");

        server.Disconnect();
        server.Dispose();
    }

    /// <summary>
    ///     Connects and starts running the client pipe. This is synchronous;
    ///         it is assumed that the server is already running.
    /// </summary>
    public static TDatum RunStructPipeClient<TDatum>(string pipeName) where TDatum : unmanaged
    {
        Console.WriteLine($"Starting client...");
        var client = InitiateClient(pipeName, pipeDirection: PipeDirection.In);
        client.Connect();

        Console.WriteLine($"Started client");

        var buffer = new byte[Unsafe.SizeOf<TDatum>()];
        var offset = 0;

        while (offset < buffer.Length)
        {
            var bytesRead = client.Read(buffer, offset, buffer.Length - offset);
            if (bytesRead == 0)
            {
                Console.WriteLine("Server disconnected");
                throw new InvalidOperationException("Server was disconnected while reading.");
            }

            offset += bytesRead;
        }

        client.Dispose();
        return MemoryToStruct<TDatum>(buffer.AsMemory());
    }

    /// <summary>
    ///     Connects and starts running the server pipe.
    /// </summary>
    /// <param name="sendAction">When called and the pipe is connected, writes the string directly to the pipe.</param>
    /// <param name="onLineReceived">Invoked with the read line every time a line is read from the pipe, from the server.</param>
    public static async Task<NamedPipeServerStream> RunPipeServer(string pipeName, Action<string> sendAction, Action<string>? onLineReceived = null)
    {
        Console.WriteLine($"Starting server...");
        var server = InitiateServer(pipeName);
        await server.WaitForConnectionAsync();
        InitialiseStreams(server, out var serverReader, out var serverWriter);
        Console.WriteLine($"Started server");

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
        Console.WriteLine($"Starting client...");
        var client = InitiateClient(pipeName);
        await client.ConnectAsync();
        InitialiseStreams(client, out var clientReader, out var clientWriter);

        Console.WriteLine($"Started client");

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
            Console.WriteLine("Pipe says: " + line);
        }

        // Pipe closed, disable the action
        sendAction = _ => { };
        Console.WriteLine("Pipe closed. Send action disabled.");
    }

    /// <summary>
    ///     Creates a <see cref="NamedPipeServerStream"/>.
    /// </summary>
    private static NamedPipeServerStream InitiateServer(string pipeName, PipeTransmissionMode transmissionMode = PipeTransmissionMode.Message, PipeDirection pipeDirection = PipeDirection.InOut)
        => new(pipeName, pipeDirection, 1, transmissionMode);

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

    public static ReadOnlyMemory<byte> StructToMemory<T>(ref T str) where T : unmanaged
    {
        var buffer = new byte[Unsafe.SizeOf<T>()];
        Unsafe.WriteUnaligned(ref buffer[0], str);
        return buffer.AsMemory(); // expose as ReadOnlyMemory<byte>
    }

    public static T MemoryToStruct<T>(ReadOnlyMemory<byte> mem) where T : unmanaged
    {
        if (mem.Length < Unsafe.SizeOf<T>())
            throw new ArgumentException("Memory is too small for struct");

        return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(mem.Span));
    }
}
