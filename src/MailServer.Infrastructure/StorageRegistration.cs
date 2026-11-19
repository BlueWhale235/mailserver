using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using MailServer.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

internal static class StorageRegistration
{
    public static IServiceCollection AddMessageStorage(this IServiceCollection services)
    {
        services.AddSingleton<FileMessageContentStore>();
        services.AddSingleton<IAmazonS3>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<StorageOptions>>().Value.S3;
            if (string.IsNullOrWhiteSpace(options.Bucket)) throw new InvalidOperationException("Storage:S3:Bucket is required.");
            var config = new AmazonS3Config
            {
                RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region),
                ForcePathStyle = options.ForcePathStyle,
                Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 600)),
                MaxErrorRetry = 2
            };
            if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
            {
                if (!Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException("Storage:S3:ServiceUrl must be an HTTP(S) URL.");
                config.ServiceURL = options.ServiceUrl;
                config.AuthenticationRegion = options.Region;
            }
            if (string.IsNullOrEmpty(options.AccessKey) != string.IsNullOrEmpty(options.SecretKey))
                throw new InvalidOperationException("S3 access key and secret key must be configured together.");
            if (string.IsNullOrEmpty(options.AccessKey)) return new AmazonS3Client(config);
            AWSCredentials credentials = string.IsNullOrEmpty(options.SessionToken)
                ? new BasicAWSCredentials(options.AccessKey, options.SecretKey)
                : new SessionAWSCredentials(options.AccessKey, options.SecretKey, options.SessionToken);
            return new AmazonS3Client(credentials, config);
        });
        services.AddSingleton<S3MessageContentStore>();
        services.AddSingleton<IMessageContentStore>(provider =>
            provider.GetRequiredService<IOptions<StorageOptions>>().Value.Provider.ToLowerInvariant() switch
            {
                "file" => provider.GetRequiredService<FileMessageContentStore>(),
                "s3" => provider.GetRequiredService<S3MessageContentStore>(),
                _ => throw new InvalidOperationException("Storage:Provider must be File or S3.")
            });
        services.AddSingleton<IMessageStorageMaintenance>(provider => (IMessageStorageMaintenance)provider.GetRequiredService<IMessageContentStore>());
        return services;
    }
}
