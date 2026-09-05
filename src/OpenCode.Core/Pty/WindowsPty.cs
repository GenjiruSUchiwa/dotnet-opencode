namespace OpenCode.Core.Pty;
using System.Buffers;
using System.IO.Pipelines;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

internal sealed class WindowsPty : IAsyncDisposable
{
    private readonly SafePseudoConsole console;
    private readonly FileStream input;
    private readonly FileStream output;
    private readonly SemaphoreSlim writing = new(1);
    private readonly Channel<byte[]> chunks = Channel.CreateUnbounded<byte[]>(new() { SingleWriter = true, SingleReader = true });
    private readonly Task reading;
    private SafeProcessHandle? process;
    private Task<uint>? completion;
    private int closing;

    private WindowsPty(SafePseudoConsole console, SafeFileHandle input, SafeFileHandle output)
    {
        this.console = console;
        this.input = new FileStream(input, FileAccess.Write, 4096, false);
        this.output = new FileStream(output, FileAccess.Read, 4096, false);
        // ConPTY requires synchronous pipes. Drain on a separate thread, including during close.
        reading = Task.Factory.StartNew(Read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal int Pid { get; private set; }
    internal ChannelReader<byte[]> Output => chunks.Reader;
    internal Task<uint> Completion => completion ?? throw new InvalidOperationException("PTY has not started.");

    internal static WindowsPty Start(string command, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string> environment)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Local PTYs require Windows 10 build 17763 or later. Unix PTYs are not implemented.");
        ConPtyNative.Check(ConPtyNative.CreatePipe(out var inputRead, out var inputWrite, 0, 0));
        using (inputRead)
        {
            SafeFileHandle? outputRead = null;
            SafeFileHandle? outputWrite = null;
            SafePseudoConsole? console = null;
            WindowsPty? terminal = null;
            try
            {
                ConPtyNative.Check(ConPtyNative.CreatePipe(out outputRead, out outputWrite, 0, 0));
                Marshal.ThrowExceptionForHR(ConPtyNative.CreatePseudoConsole(new() { X = 80, Y = 24 }, inputRead, outputWrite, 0, out console));
                terminal = new WindowsPty(console, inputWrite, outputRead);
                terminal.Spawn(command, args, cwd, environment);
                return terminal;
            }
            catch
            {
                // The output pump remains active while closing the pseudoconsole.
                console?.Dispose();
                inputWrite.Dispose();
                outputWrite?.Dispose();
                if (terminal is not null) terminal.reading.GetAwaiter().GetResult();
                outputRead?.Dispose();
                terminal?.input.Dispose();
                terminal?.output.Dispose();
                terminal?.process?.Dispose();
                throw;
            }
            finally { outputWrite?.Dispose(); }
        }
    }

    private void Spawn(string command, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string> environment)
    {
        nuint size = 0;
        ConPtyNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var attributes = Marshal.AllocHGlobal(checked((nint)size));
        var initialized = false;
        var commandLine = Marshal.StringToHGlobalUni(string.Join(' ', new[] { command }.Concat(args).Select(Quote)));
        var block = Marshal.StringToHGlobalUni(string.Join('\0', environment.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}")) + "\0\0");
        try
        {
            ConPtyNative.Check(ConPtyNative.InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
            initialized = true;
            ConPtyNative.Check(ConPtyNative.UpdateProcThreadAttribute(attributes, 0, 0x00020016, console, (nuint)nint.Size, 0, 0));
            var startup = new ConPtyNative.StartupInfoEx { StartupInfo = new() { Size = (uint)Marshal.SizeOf<ConPtyNative.StartupInfoEx>() }, Attributes = attributes };
            // ProcessStartInfo in the installed .NET 11 SDK cannot supply STARTUPINFOEX attributes.
            ConPtyNative.Check(ConPtyNative.CreateProcess(null, commandLine, 0, 0, false, 0x00080000 | 0x00000400, block, cwd, ref startup, out var information));
            process = new SafeProcessHandle(information.Process, true);
            using var thread = new SafeThreadHandle(information.Thread);
            Pid = checked((int)information.ProcessId);
            completion = Task.Factory.StartNew(() =>
            {
                if (ConPtyNative.WaitForSingleObject(process, uint.MaxValue) != 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                ConPtyNative.Check(ConPtyNative.GetExitCodeProcess(process, out var code));
                console.Dispose();
                return code;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally
        {
            if (initialized) ConPtyNative.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(block);
        }
    }

    private void Read()
    {
        var reader = PipeReader.Create(output, new StreamPipeReaderOptions(bufferSize: 65536, minimumReadSize: 65536, leaveOpen: true));
        try
        {
            while (true)
            {
                var result = reader.ReadAsync().AsTask().GetAwaiter().GetResult();
                try { if (!result.Buffer.IsEmpty) chunks.Writer.TryWrite(result.Buffer.ToArray()); }
                finally { reader.AdvanceTo(result.Buffer.End); }
                if (result.IsCompleted) break;
            }
            chunks.Writer.TryComplete();
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 109 or 232) { chunks.Writer.TryComplete(); }
        catch (Exception error) { chunks.Writer.TryComplete(error); }
        finally { reader.Complete(); }
    }

    internal async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await writing.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref closing) != 0 || Completion.IsCompleted) return;
            await input.WriteAsync(data, cancellationToken);
            await input.FlushAsync(cancellationToken);
        }
        finally { writing.Release(); }
    }

    internal void Resize(short cols, short rows) => Marshal.ThrowExceptionForHR(ConPtyNative.ResizePseudoConsole(console, new() { X = cols, Y = rows }));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        await Task.Run(console.Dispose);
        await Completion;
        await reading;
        await writing.WaitAsync();
        try { input.Dispose(); output.Dispose(); process?.Dispose(); }
        finally { writing.Release(); }
    }

    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("PTY command and arguments cannot contain NUL.");
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
