using System.Security.Cryptography;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed class FileMessageContentStore(
    IOptions<StorageOptions> options,
    IDbContextFactory<MailDbContext> dbFactory) : IMessageContentStore
{
    private readonly StorageOptions _options = options.Value;

    public async Task<ContentDescriptor> SaveAsync(Stream content, CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Stage("storage.write");
        var root = Path.GetFullPath(_options.RootPath);
        var temporaryDirectory = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(temporaryDirectory, $"{Guid.NewGuid():N}.eml");

        long size = 0;
        byte[] hash;
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > _options.MaximumMessageBytes)
                    {
                        throw new InvalidDataException($"Message exceeds the configured {_options.MaximumMessageBytes} byte limit.");
                    }

                    sha256.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
                hash = sha256.GetHashAndReset();
            }

            var key = Convert.ToHexStringLower(hash);
            var finalDirectory = Path.Combine(root, key[..2], key[2..4]);
            Directory.CreateDirectory(finalDirectory);
            var finalPath = Path.Combine(finalDirectory, $"{key}.eml");

            if (!File.Exists(finalPath))
            {
                File.Move(temporaryPath, finalPath);
            }
            else
            {
                File.Delete(temporaryPath);
            }

            return new ContentDescriptor(key, size);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        var path = GetPath(key);
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public async Task DeleteIfUnreferencedAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Messages.AnyAsync(x => x.ContentKey == key, cancellationToken))
        {
            return;
        }

        var path = GetPath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string key)
    {
        ValidateKey(key);
        return Path.Combine(Path.GetFullPath(_options.RootPath), key[..2], key[2..4], $"{key}.eml");
    }

    private static void ValidateKey(string key)
    {
        if (key.Length != 64 || key.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Invalid content key.", nameof(key));
        }
    }
}
