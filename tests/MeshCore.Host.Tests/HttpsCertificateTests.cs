using System.Net;
using System.Security.Cryptography.X509Certificates;
using MeshCore.Host.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;

namespace MeshCore.Host.Tests;

public sealed class HttpsCertificateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcc-certs", Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();

    private HttpsCertificates Load() => HttpsCertificates.Load(new HttpsOptions(), _dir, _protection, TimeProvider.System);

    [Fact]
    public void Creates_a_local_authority_and_a_site_certificate_signed_by_it()
    {
        var certs = Load();

        Assert.True(certs.Created);
        Assert.NotNull(certs.Authority);
        Assert.True(certs.Authority.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal(certs.Authority.Subject, certs.SiteCertificate.Issuer);
        Assert.True(certs.SiteCertificate.HasPrivateKey);
        Assert.False(certs.SiteCertificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Contains(certs.SiteCertificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(),
            o => o.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void Site_certificate_covers_localhost_machine_name_and_lan_addresses()
    {
        var site = Load().SiteCertificate;
        var san = site.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        Assert.Contains("localhost", san.EnumerateDnsNames());
        Assert.Contains(Environment.MachineName, san.EnumerateDnsNames(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(IPAddress.Loopback, san.EnumerateIPAddresses());
        Assert.True(HttpsCertificates.Covers(site, HttpsCertificates.HostNames()));
        Assert.True(site.NotAfter - DateTime.Now <= TimeSpan.FromDays(731));
    }

    [Fact]
    public void A_device_that_trusts_the_authority_accepts_the_site_certificate()
    {
        var certs = Load();
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(X509CertificateLoader.LoadCertificate(certs.AuthorityDer!)); // only the public part, as a phone gets it
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));

        Assert.True(chain.Build(certs.SiteCertificate), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));
    }

    [Fact]
    public void Certificates_are_reused_on_the_next_start()
    {
        var first = Load();
        var second = Load();

        Assert.False(second.Created);
        Assert.Equal(first.SiteCertificate.Thumbprint, second.SiteCertificate.Thumbprint);
        Assert.Equal(first.Authority!.Thumbprint, second.Authority!.Thumbprint);
        Assert.True(second.SiteCertificate.HasPrivateKey);
    }

    [Fact]
    public void Unreadable_saved_certificates_are_replaced()
    {
        Load();
        // Different Data Protection keys (e.g. the key folder was lost): the saved passwords can't be decrypted.
        var other = HttpsCertificates.Load(new HttpsOptions(), _dir, new EphemeralDataProtectionProvider(), TimeProvider.System);

        Assert.True(other.Created);
        Assert.True(other.SiteCertificate.HasPrivateKey);
    }

    [Fact]
    public void Private_keys_are_never_written_unprotected()
    {
        Load();

        var keyFiles = Directory.GetFiles(_dir, "*.key");
        Assert.Equal(2, keyFiles.Length);
        Assert.All(keyFiles, f => Assert.StartsWith("CfDJ8", File.ReadAllText(f), StringComparison.Ordinal)); // Data Protection payloads
        Assert.Empty(Directory.GetFiles(_dir, "*.pem"));
    }

    [Fact]
    public void Your_own_certificate_is_used_as_is()
    {
        var generated = Load().SiteCertificate;
        var path = Path.Combine(_dir, "own.pfx");
        File.WriteAllBytes(path, generated.Export(X509ContentType.Pkcs12, "pw"));

        var own = HttpsCertificates.Load(new HttpsOptions { CertificatePath = path, CertificatePassword = "pw" }, _dir, _protection, TimeProvider.System);

        Assert.Null(own.Authority);
        Assert.Null(own.AuthorityDer);
        Assert.Equal(generated.Thumbprint, own.SiteCertificate.Thumbprint);
    }

    [Fact]
    public async Task Kestrel_completes_a_real_tls_handshake_with_a_freshly_created_certificate()
    {
        // Regression: a just-created certificate has an in-memory key that Windows' SChannel can't use.
        var certs = Load();
        Assert.True(certs.Created);
        // Empty content root, so the app's appsettings.json (with its fixed ports) isn't picked up.
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = _dir });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certs.SiteCertificate)));
        await using var app = builder.Build();
        app.MapGet("/", () => "ok");
        await app.StartAsync();
        var address = app.Urls.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);

        var authority = X509CertificateLoader.LoadCertificate(certs.AuthorityDer!);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(authority);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return cert is not null && chain.Build(cert);
            },
        };
        using var client = new HttpClient(handler);

        Assert.Equal("ok", await client.GetStringAsync(address));
        await app.StopAsync();
    }

    [Fact]
    public void A_certificate_missing_a_name_is_not_accepted_as_covering_it()
    {
        var site = Load().SiteCertificate;

        Assert.False(HttpsCertificates.Covers(site, ["localhost", "192.0.2.77"]));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class CertificatePageTests
{
    [Fact]
    public async Task Certificate_help_is_reachable_before_setup_and_without_signing_in()
    {
        using var app = new ApiFactory();
        var browser = new Browser(app);

        var page = await browser.GetAsync("/Account/Certificate");
        var file = await browser.GetAsync("/Account/CertificateFile");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("HTTPS is turned off", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal); // tests run without HTTPS
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
    }
}
