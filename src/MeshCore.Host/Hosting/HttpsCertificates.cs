using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;

namespace MeshCore.Host.Hosting;

/// <summary>The <c>Web:Https</c> configuration section.</summary>
public sealed class HttpsOptions
{
    public const string SectionName = "Web:Https";

    /// <summary>Serve the website over HTTPS too. Phones need it for secure cookies, offline support and app install.</summary>
    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 5443;

    /// <summary>Send website visitors on plain HTTP to HTTPS (the certificate help page stays reachable over HTTP).</summary>
    public bool RedirectHttp { get; set; } = true;

    /// <summary>Use your own certificate (.pfx) instead of the generated one.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }
}

/// <summary>
/// The website's HTTPS certificates. Unless you configure your own, the app makes a small local certificate
/// authority (10 years) and a site certificate signed by it (2 years) covering localhost, this computer's name and its
/// LAN addresses. Phones install the authority certificate once; when the addresses change only the site certificate
/// is reissued, so phones keep trusting the site. Private keys are saved as .pfx files whose passwords are encrypted
/// with Data Protection (DPAPI on Windows).
/// </summary>
public sealed class HttpsCertificates
{
    private const string CaFile = "meshcore-ca.pfx";
    private const string SiteFile = "meshcore-site.pfx";
    private static readonly TimeSpan CaLifetime = TimeSpan.FromDays(3650);
    private static readonly TimeSpan SiteLifetime = TimeSpan.FromDays(730);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private HttpsCertificates(X509Certificate2 site, X509Certificate2? authority, bool created)
    {
        SiteCertificate = site;
        Authority = authority;
        Created = created;
    }

    /// <summary>The certificate Kestrel serves.</summary>
    public X509Certificate2 SiteCertificate { get; }

    /// <summary>The local authority (public part is offered for download); null when using your own certificate.</summary>
    public X509Certificate2? Authority { get; }

    /// <summary>True if this start created or reissued the site certificate.</summary>
    public bool Created { get; }

    /// <summary>The authority certificate in DER form, for installing on phones.</summary>
    public byte[]? AuthorityDer => Authority?.Export(X509ContentType.Cert);

    public static HttpsCertificates Load(HttpsOptions options, string directory, IDataProtectionProvider dataProtection, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CertificatePath is { Length: > 0 } path)
        {
            return new HttpsCertificates(X509CertificateLoader.LoadPkcs12FromFile(path, options.CertificatePassword), null, false);
        }

        Directory.CreateDirectory(directory);
        var protector = dataProtection.CreateProtector("MeshCore.Https.CertificatePassword.v1");
        var now = time.GetUtcNow();
        var names = HostNames();

        var ca = LoadPfx(directory, CaFile, protector);
        if (ca is null || ca.NotAfter.ToUniversalTime() - now.UtcDateTime < RenewBefore)
        {
            SavePfx(directory, CaFile, CreateAuthority(now), protector);
            ca = LoadPfx(directory, CaFile, protector) ?? throw new InvalidOperationException("The new certificate authority couldn't be read back.");
            DeleteIfExists(Path.Combine(directory, SiteFile)); // a new authority means a new site certificate
        }

        var site = LoadPfx(directory, SiteFile, protector);
        var created = false;
        if (site is null || site.NotAfter.ToUniversalTime() - now.UtcDateTime < RenewBefore || !Covers(site, names) || site.Issuer != ca.Subject)
        {
            SavePfx(directory, SiteFile, CreateSite(ca, names, now), protector);
            // Load it back from the file: a freshly created certificate's key only exists in memory, and Windows'
            // TLS stack (SChannel, used by Kestrel) can't use in-memory keys. The handshake would just fail.
            site = LoadPfx(directory, SiteFile, protector)
                   ?? throw new InvalidOperationException("The new HTTPS certificate couldn't be read back.");
            created = true;
        }
        return new HttpsCertificates(site, ca, created);
    }

    /// <summary>Names and addresses the site certificate must cover.</summary>
    public static IReadOnlyList<string> HostNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "::1", Environment.MachineName, $"{Environment.MachineName}.local" };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            {
                foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    {
                        names.Add(address.ToString());
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No interface list available: localhost and the machine name still work.
        }
        return [.. names];
    }

    internal static bool Covers(X509Certificate2 cert, IEnumerable<string> names)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return false;
        }
        var have = san.EnumerateDnsNames().Concat(san.EnumerateIPAddresses().Select(ip => ip.ToString())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.All(have.Contains);
    }

    private static X509Certificate2 CreateAuthority(DateTimeOffset now)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN=MeshCore Computer Client local CA ({Environment.MachineName}), O=MeshCore Computer Client",
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now.AddDays(-1), now + CaLifetime);
    }

    private static X509Certificate2 CreateSite(X509Certificate2 ca, IReadOnlyList<string> names, DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Environment.MachineName}, O=MeshCore Computer Client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server auth
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var notAfter = now + SiteLifetime < ca.NotAfter ? now + SiteLifetime : new DateTimeOffset(ca.NotAfter);
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F; // positive
        using var signed = request.Create(ca, now.AddDays(-1), notAfter, serial);
        return signed.CopyWithPrivateKey(key);
    }

    private static X509Certificate2? LoadPfx(string directory, string file, IDataProtector protector)
    {
        var pfx = Path.Combine(directory, file);
        var pass = pfx + ".key";
        if (!File.Exists(pfx) || !File.Exists(pass))
        {
            return null;
        }
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(pfx, protector.Unprotect(File.ReadAllText(pass)), X509KeyStorageFlags.Exportable);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // Unreadable (e.g. data protection keys lost): make a new one.
        }
    }

    private static void SavePfx(string directory, string file, X509Certificate2 cert, IDataProtector protector)
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = Path.Combine(directory, file);
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12, password));
        File.WriteAllText(pfx + ".key", protector.Protect(password));
    }

    private static void DeleteIfExists(string path)
    {
        File.Delete(path);
        File.Delete(path + ".key");
    }
}
