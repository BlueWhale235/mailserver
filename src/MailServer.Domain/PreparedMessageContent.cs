using System.Runtime.InteropServices;

namespace MailServer.Domain;

// Owned SMTP spool: disposal deletes only the source file until ownership is transferred.
public sealed class PreparedMessageContent : Stream
{
    private FileStream? _source;
    private string? _path;
    public ContentDescriptor Descriptor { get; }
    public PreparedMessageContent(string path, ContentDescriptor descriptor)
    {
        _path = Path.GetFullPath(path); Descriptor = descriptor; _source = Open(_path);
    }
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    public bool TryTransferTo(string destination)
    {
        if (_path is null) throw new ObjectDisposedException(nameof(PreparedMessageContent));
        var sourcePath = _path;
        // File.Move can silently copy across volumes. Only use it after proving
        // the Linux device identity; unsupported platforms retain the verified stream copy.
        if (!SameDevice(sourcePath, Path.GetDirectoryName(Path.GetFullPath(destination))!)) return false;
        _source!.Dispose(); _source = null;
        try { File.Move(sourcePath, destination); _path = null; return true; }
        catch (IOException) { _source = Open(sourcePath); return false; }
    }
    private static bool SameDevice(string source, string directory)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            return Statx(-100, source, 0, 0x7ff, out var from) == 0
                && Statx(-100, directory, 0, 0x7ff, out var to) == 0
                && from.DeviceMajor == to.DeviceMajor && from.DeviceMinor == to.DeviceMinor;
        }
        catch (EntryPointNotFoundException) { return false; }
        catch (DllNotFoundException) { return false; }
    }
    // Linux statx has a fixed 256-byte ABI; device fields are unconditional.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out LinuxStatx status);
    public override bool CanRead => _source?.CanRead == true;
    public override bool CanSeek => _source?.CanSeek == true;
    public override bool CanWrite => false;
    public override long Length => _source!.Length;
    public override long Position { get => _source!.Position; set => _source!.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => _source!.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => _source!.ReadAsync(buffer, token);
    public override long Seek(long offset, SeekOrigin origin) => _source!.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _source?.Dispose(); _source = null; if (_path is { } path) { _path = null; File.Delete(path); } }
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
