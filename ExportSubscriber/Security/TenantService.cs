﻿using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace ExportSubscriber.Security
{
    /// <summary>
    /// Client for TenantService which provides you with secrets to connect to the ServiceBus+Blob export queue.
    /// 
    /// It does this by doing a HTTP-request to TenantService using a certificate as credentials.
    /// </summary>
    public class TenantService
    {
        private readonly Uri _tenantServiceUrl;
        private readonly string _integrationName;
        private readonly string _certificateCommonName;
        private readonly string _resourceId;
        private readonly string _authorityUrl;
        private readonly string _clientId;
        private IConfidentialClientApplication _authContext;

        public TenantService(Config config)
        {
            _tenantServiceUrl = new Uri(config.TenantServiceUrl);
            if (_tenantServiceUrl.Segments.Length == 1)
                _tenantServiceUrl = new Uri(_tenantServiceUrl, $"/api/external/integration/export/temporaryendpoints");
            _integrationName = config.IntegrationPartnerName;
            _certificateCommonName = config.TenantServiceCertificateName;
            _resourceId = config.TenantServiceResourceId;
            _authorityUrl = config.AuthorityUrl;
            _clientId = config.ClientId;
        }

        public async Task<ConnectionSecrets> GetSecrets()
        {
            if (_authContext == null)
            {
                var certificate = GetFromCertificateStore(_certificateCommonName);

                _authContext = ConfidentialClientApplicationBuilder.Create(_clientId)
                    .WithAuthority(_authorityUrl)
                    .WithCertificate(certificate)
                    .Build();
            }

            Console.WriteLine($"Acquiring token from '{_authorityUrl}' using client id '{_clientId}'...");
            var scopes = new[] { $"{_resourceId}/.default" };
            var authResult = await _authContext.AcquireTokenForClient(scopes).ExecuteAsync();

            Console.WriteLine($"Acquiring temporary connection secrets from {_tenantServiceUrl.Host}...");
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authResult.AccessToken);
            var result = await httpClient.PostAsJsonAsync(
                _tenantServiceUrl,
                new { integrationName = _integrationName, useNewSBConnectionStringFormat = true });
            result.EnsureSuccessStatusCode(); // If this gives you 403, you have not been granted the proper permissions yet.
            var secrets = await result.Content.ReadFromJsonAsync<ConnectionSecrets>();
            secrets.TimeUpdatedUtc = DateTimeOffset.UtcNow;
            return secrets;
        }

        private static X509Certificate2 GetFromCertificateStore(string commonName)
        {
            using (var certStore = new X509Store(StoreName.My, StoreLocation.CurrentUser))
            {
                certStore.Open(OpenFlags.ReadOnly);
                var certs = certStore.Certificates.Find(X509FindType.FindBySubjectName, $"{commonName}", false)
                    .OfType<X509Certificate2>()
                    .Where(c => c.SubjectName.Name?.Contains($"CN={commonName}") ??
                                false) // Make sure it is actually the CN
                    .ToArray();
                certStore.Close();

                if (certs.Length == 0) throw new Exception($"Cert '{commonName}' not found");

                // Return the newest certificate, enables certificate rotation.
                return certs.OrderByDescending(c => c.NotAfter).First();
            }
        }
    }
}
