using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace MioReel.Desktop.Services;

/// <summary>
/// 跨平台单实例进程互斥锁与唤醒已有实例通信（Windows Mutex + Linux 文件锁 + NamedPipe 唤醒）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _ownsMutex;
    private FileStream? _lockFile;
    private CancellationTokenSource? _listenCts;
    private Task? _listenTask;
    private bool _disposed;

    private SingleInstanceGuard(string pipeName, Mutex? mutex, bool ownsMutex, FileStream? lockFile)
    {
        _pipeName = pipeName;
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _lockFile = lockFile;
    }

    public static SingleInstanceGuard? TryAcquire(string appId)
    {
        var userSuffix = SanitizeSegment(Environment.UserName);
        var pipeName = $"{appId}.Activate.{userSuffix}";

        if (OperatingSystem.IsWindows())
        {
            var mutexName = $@"Local\{appId}.SingleInstance.{userSuffix}";
            var mutex = new Mutex(initiallyOwned: true, name: mutexName, out var createdNew);
            var acquired = createdNew;
            if (!acquired)
            {
                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            return new SingleInstanceGuard(pipeName, mutex, ownsMutex: true, lockFile: null);
        }

        var lockPath = Path.Combine(Path.GetTempPath(), $"{appId}-{userSuffix}.lock");
        try
        {
            var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var pidBytes = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString());
            stream.SetLength(0);
            stream.Write(pidBytes);
            stream.Flush();
            return new SingleInstanceGuard(pipeName, mutex: null, ownsMutex: false, lockFile: stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SignalExistingInstance(string appId)
    {
        var userSuffix = SanitizeSegment(Environment.UserName);
        var pipeName = $"{appId}.Activate.{userSuffix}";
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(600);
            client.WriteByte(1);
            client.Flush();
        }
        catch
        {
            // Ignore when the primary instance is still initializing or already exiting.
        }
    }

    public void StartListening(Action onActivate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_listenCts is not null) return;

        _listenCts = new CancellationTokenSource();
        var ct = _listenCts.Token;
        _listenTask = Task.Run(() => ListenLoopAsync(onActivate, ct), ct);
    }

    private async Task ListenLoopAsync(Action onActivate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);
                _ = server.ReadByte();
                onActivate();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(100, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "default";
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _listenCts?.Cancel();
        try { _listenTask?.Wait(300); } catch { }
        _listenCts?.Dispose();

        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _ownsMutex = false;
            }
            _mutex.Dispose();
            _mutex = null;
        }

        _lockFile?.Dispose();
        _lockFile = null;
    }
}
