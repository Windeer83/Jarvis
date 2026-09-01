using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Jarvis.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Core;

internal sealed record MobileLanIdentity(
    string Endpoint,
    string CertificateFingerprint,
    X509Certificate2 Certificate);

internal static class MobileLanIdentityFactory
{
    private const string PasswordKey = "lan-certificate-password";

    public static async Task<MobileLanIdentity> LoadOrCreateAsync(
        string dataDirectory,
        WindowsCredentialStore credentialStore,
        CancellationToken cancellationToken = default)
    {
        var certificatePath = Path.Combine(dataDirectory, "jarvis-mobile-lan.pfx");
        var password = await credentialStore.ReadAsync(PasswordKey, cancellationToken)
            .ConfigureAwait(false);
        X509Certificate2 certificate;
        if (File.Exists(certificatePath) && !string.IsNullOrWhiteSpace(password))
        {
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(
                    certificatePath, password,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            catch (CryptographicException)
            {
                certificate = await CreateAsync(
                    certificatePath, credentialStore, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            certificate = await CreateAsync(
                certificatePath, credentialStore, cancellationToken).ConfigureAwait(false);
        }

        var address = MobileLanAddressResolver.Resolve();
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return new MobileLanIdentity(
            $"https://{address}:{MobileProtocol.DefaultPort}", fingerprint, certificate);
    }

    private static async Task<X509Certificate2> CreateAsync(
        string path,
        WindowsCredentialStore credentialStore,
        CancellationToken cancellationToken)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=Jarvis Local Mobile Sync", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("jarvis.local");
        foreach (var address in MobileLanAddressResolver.AllPrivateAddresses()) san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await credentialStore.SaveAsync(PasswordKey, password, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, generated.Export(X509ContentType.Pfx, password), cancellationToken)
            .ConfigureAwait(false);
        return X509CertificateLoader.LoadPkcs12FromFile(
            path, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
}

internal static class MobileLanAddressResolver
{
    public static string Resolve()
    {
        var preferred = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or
                                  NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
            .Where(adapter => adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any)))
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(value => value.Address)
            .FirstOrDefault(IsPrivateIpv4);
        return (preferred ?? AllPrivateAddresses().FirstOrDefault() ?? IPAddress.Loopback).ToString();
    }

    public static IReadOnlyList<IPAddress> AllPrivateAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                              adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(value => value.Address)
            .Where(IsPrivateIpv4)
            .Distinct()
            .ToArray();

    private static bool IsPrivateIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }
}

internal sealed class MobileLanHost(
    MobileLanIdentity identity,
    MobileSyncModule module,
    SupervisionModule supervision) : IAsyncDisposable
{
    private WebApplication? _application;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_application is not null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(MobileLanHost).Assembly.FullName,
            Args = []
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 256 * 1024;
            options.ListenAnyIP(MobileProtocol.DefaultPort, listen =>
                listen.UseHttps(identity.Certificate));
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = MobileProtocol.Json.PropertyNamingPolicy;
            foreach (var converter in MobileProtocol.Json.Converters)
                options.SerializerOptions.Converters.Add(converter);
        });

        var app = builder.Build();
        app.MapGet("/v1/health", () => Results.Json(new
        {
            protocolVersion = MobileProtocol.Version,
            service = "jarvis-mobile-sync"
        }));
        app.MapPost("/v1/pair", async (MobilePairRequest request, CancellationToken token) =>
        {
            try
            {
                return Results.Json(await module.PairAsync(request, token).ConfigureAwait(false),
                    MobileProtocol.Json);
            }
            catch (MobileProtocolException exception)
            {
                return Results.Json(new { errorCode = exception.Code, message = exception.Message },
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
        app.MapPost("/v1/sync", async (HttpRequest request, MobileSyncRequest body, CancellationToken token) =>
        {
            var authorization = request.Headers.Authorization.ToString();
            var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? authorization[7..].Trim()
                : "";
            try
            {
                var snapshot = await supervision.GetSnapshotAsync(token).ConfigureAwait(false);
                return Results.Json(
                    await module.SynchronizeAsync(bearer, body, snapshot, token).ConfigureAwait(false),
                    MobileProtocol.Json);
            }
            catch (MobileProtocolException exception)
            {
                var status = exception.Code == "unauthorized"
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status400BadRequest;
                return Results.Json(new { errorCode = exception.Code, message = exception.Message },
                    statusCode: status);
            }
        });
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _application = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is null) return;
        await _application.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        _application = null;
    }
}
