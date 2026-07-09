using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MailServer.Infrastructure;

public sealed class CertificatesOptions
{
    public const string SectionName = "Certificates";
    public string? DefaultMailHostName { get; set; }
    public bool AllowSingleCertificateFallback { get; set; } = true;
    public PemCertificateOptions Admin { get; set; } = new();
}

public sealed class PemCertificateOptions
{
    public string? CertificatePath { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassword { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(CertificatePath)
        && !string.IsNullOrWhiteSpace(PrivateKeyPath);
}

public sealed class LoadedPemCertificate : IDisposable
{
    public LoadedPemCertificate(
        X509Certificate2 certificate,
        X509Certificate2Collection intermediates,
        SslStreamCertificateContext sslContext)
    {
        Certificate = certificate;
        Intermediates = intermediates;
        SslContext = sslContext;
    }

    public X509Certificate2 Certificate { get; }
    public X509Certificate2Collection Intermediates { get; }
    public SslStreamCertificateContext SslContext { get; }

    public void Dispose()
    {
        Certificate.Dispose();
        foreach (var certificate in Intermediates)
        {
            certificate.Dispose();
        }
    }
}

public static class PemCertificateLoader
{
    public static LoadedPemCertificate? LoadOptional(PemCertificateOptions options, string purpose)
    {
        if (!options.IsConfigured)
        {
            if (!string.IsNullOrWhiteSpace(options.CertificatePath)
                || !string.IsNullOrWhiteSpace(options.PrivateKeyPath))
            {
                throw new InvalidOperationException(
                    $"{purpose} certificate requires both CertificatePath and PrivateKeyPath.");
            }

            return null;
        }

        return Load(options, purpose);
    }

    public static LoadedPemCertificate Load(PemCertificateOptions options, string purpose)
    {
        var certificatePath = Path.GetFullPath(options.CertificatePath!);
        var privateKeyPath = Path.GetFullPath(options.PrivateKeyPath!);
        if (!File.Exists(certificatePath))
            throw new FileNotFoundException($"{purpose} PEM certificate was not found.", certificatePath);
        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException($"{purpose} PEM private key was not found.", privateKeyPath);

        var importedLeaf = string.IsNullOrEmpty(options.PrivateKeyPassword)
            ? X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath)
            : X509Certificate2.CreateFromEncryptedPemFile(
                certificatePath,
                options.PrivateKeyPassword,
                privateKeyPath);
        var leaf = importedLeaf;
        if (OperatingSystem.IsWindows())
        {
            var pkcs12 = importedLeaf.Export(X509ContentType.Pkcs12);
            leaf = X509CertificateLoader.LoadPkcs12(
                pkcs12,
                password: null,
                X509KeyStorageFlags.UserKeySet
                | X509KeyStorageFlags.Exportable);
            CryptographicOperations.ZeroMemory(pkcs12);
            importedLeaf.Dispose();
        }
        if (!leaf.HasPrivateKey)
        {
            leaf.Dispose();
            throw new InvalidOperationException($"{purpose} PEM certificate does not contain a matching private key.");
        }

        var now = DateTimeOffset.UtcNow;
        if (now < leaf.NotBefore || now > leaf.NotAfter)
        {
            var validity = $"{leaf.NotBefore:O} - {leaf.NotAfter:O}";
            leaf.Dispose();
            throw new InvalidOperationException($"{purpose} certificate is outside its validity period ({validity}).");
        }

        var allCertificates = new X509Certificate2Collection();
        allCertificates.ImportFromPemFile(certificatePath);
        var intermediates = new X509Certificate2Collection();
        foreach (var certificate in allCertificates)
        {
            if (!string.Equals(certificate.Thumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                intermediates.Add(certificate);
            }
            else
            {
                certificate.Dispose();
            }
        }

        try
        {
            SslStreamCertificateContext context;
            try
            {
                context = SslStreamCertificateContext.Create(leaf, intermediates, offline: false);
            }
            catch (CryptographicException) when (intermediates.Count > 0)
            {
                context = SslStreamCertificateContext.Create(leaf, additionalCertificates: null, offline: false);
            }
            return new LoadedPemCertificate(leaf, intermediates, context);
        }
        catch
        {
            leaf.Dispose();
            foreach (var certificate in intermediates) certificate.Dispose();
            throw;
        }
    }
}
