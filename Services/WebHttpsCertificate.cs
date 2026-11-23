// -----------------------------------------------------------------------------
// Project:     HUDnopy — Elite Dangerous Telemetry HUD & Copilot
// File:        Services/WebHttpsCertificate.cs
// Author:      AlonsoLP
// Copyright:   Copyright (c) 2026 AlonsoLP
// License:     GPL-3.0-only (see LICENSE file in the repository root)
// Description: Self-signed HTTPS certificate generator and DPAPI-protected store for the Web HUD.
// -----------------------------------------------------------------------------

using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace HUDnopy.Services;

/// <summary>
/// Self-signed HTTPS certificate generator and DPAPI-protected store for the Web HUD.
/// </summary>
internal static class WebHttpsCertificate
{
    private sealed record StoredCertificate(string Pfx, string[] Addresses);
    private static readonly object CertCacheLock = new();
    private static (X509Certificate2 Cert, string[] Addresses)? _inMemoryCachedCert;

    public static X509Certificate2 LoadOrCreate(IEnumerable<IPAddress> addresses)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web-certificate.dat");
        string[] required = addresses.Select(address => address.ToString()).Distinct(StringComparer.Ordinal).ToArray();
        lock (CertCacheLock)
        {
            if (_inMemoryCachedCert is { } cached &&
                cached.Cert.HasPrivateKey &&
                cached.Cert.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7) &&
                required.All(address => cached.Addresses.Contains(address, StringComparer.Ordinal)))
            {
                return cached.Cert;
            }
        }
        if (File.Exists(path))
        {
            byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                var stored = JsonSerializer.Deserialize<StoredCertificate>(plaintext)
                    ?? throw new InvalidDataException("El certificado HTTPS guardado no es válido.");
                byte[] pfx = Convert.FromBase64String(stored.Pfx);
                try
                {
                    var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
                    if (certificate.HasPrivateKey && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7) &&
                        required.All(address => stored.Addresses.Contains(address, StringComparer.Ordinal)))
                    {
                        lock (CertCacheLock)
                            _inMemoryCachedCert = (certificate, stored.Addresses);
                        return certificate;
                    }
                    certificate.Dispose();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pfx);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=HUDnopy", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var address in addresses.Distinct())
            names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        byte[] exported = created.Export(X509ContentType.Pfx);
        try
        {
            byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(new StoredCertificate(Convert.ToBase64String(exported), required));
            try
            {
                File.WriteAllBytes(path, ProtectedData.Protect(serialized, null, DataProtectionScope.CurrentUser));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
            var loadedCert = X509CertificateLoader.LoadPkcs12(exported, null, X509KeyStorageFlags.UserKeySet);
            lock (CertCacheLock)
                _inMemoryCachedCert = (loadedCert, required);
            return loadedCert;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(exported);
        }
    }

    public static bool IsTrustedInCurrentUserRoot(X509Certificate2? certificate)
    {
        if (certificate is null)
            return false;
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates
                .Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
                .Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrustInCurrentUserRoot(X509Certificate2 certificate, out string message)
    {
        try
        {
            // Export public certificate ONLY (no private key) and install into CurrentUser\Root
            byte[] publicBytes = certificate.Export(X509ContentType.Cert);
            using var publicCert = X509CertificateLoader.LoadCertificate(publicBytes);
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, publicCert.Thumbprint, validOnly: false).Count == 0)
                store.Add(publicCert);
            message = "HTTPS certificate trusted in Windows (restart browser/PWA once to clear cached warning).";
            return true;
        }
        catch (CryptographicException)
        {
            message = "Certificate trust cancelled by user.";
            return false;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("WebHttpsCertificate", $"Failed to trust cert: {ex.Message}");
            message = $"Could not trust certificate: {ex.Message}";
            return false;
        }
    }

    public static bool UntrustFromCurrentUserRoot(X509Certificate2 certificate, out string message)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false);
            foreach (var match in matches)
                store.Remove(match);
            message = "HTTPS certificate removed from Windows CurrentUser Root store.";
            return true;
        }
        catch (CryptographicException)
        {
            message = "Certificate removal cancelled by user.";
            return false;
        }
        catch (Exception ex)
        {
            AppDiagnosticsLogger.Warn("WebHttpsCertificate", $"Failed to remove cert: {ex.Message}");
            message = $"Could not remove certificate: {ex.Message}";
            return false;
        }
    }
}
