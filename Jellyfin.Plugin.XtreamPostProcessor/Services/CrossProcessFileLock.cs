using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.XtreamPostProcessor.Services;

internal sealed class CrossProcessFileLock : IDisposable
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private FileStream? _stream;

    private CrossProcessFileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static CrossProcessFileLock? TryAcquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Lock path has no directory: {path}"));
        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                OperatingSystem.IsWindows() ? FileShare.None : FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return null;
        }

        if (!OperatingSystem.IsWindows())
        {
            if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), LockExclusive | LockNonBlocking) != 0)
            {
                stream.Dispose();
                return null;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new CrossProcessFileLock(stream);
    }

    public static async Task<CrossProcessFileLock> AcquireAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = TryAcquire(path);
            if (acquired is not null)
            {
                return acquired;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), LockUnlock);
        }

        stream.Dispose();
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fileDescriptor, int operation);
}