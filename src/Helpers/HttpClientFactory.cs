using System;
using System.Net;
using System.Net.Http;

namespace NugetUtility.Helpers;

internal static class HttpClientFactory
{
    private static HttpClient _httpClient;
    private const int maxRedirects = 5;

    public static HttpClient GetHttpClient(PackageOptions packageOptions)
    {
        if (_httpClient is null)
        {
            var httpClientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = maxRedirects,
            };

            if (!string.IsNullOrWhiteSpace(packageOptions.ProxyURL))
            {
                var myProxy = new WebProxy(new Uri(packageOptions.ProxyURL));

                if (packageOptions.ProxySystemAuth)
                {
                    myProxy.Credentials = CredentialCache.DefaultCredentials;
                }

                httpClientHandler.Proxy = myProxy;
            }

            if (packageOptions.IgnoreSslCertificateErrors)
            {
                httpClientHandler.ServerCertificateCustomValidationCallback = Extractor.IgnoreSslCertificateErrorCallback;
            }

            _httpClient = new HttpClient(httpClientHandler)
            {
                BaseAddress = new Uri(Extractor.NugetUrl),
                Timeout = TimeSpan.FromSeconds(packageOptions.Timeout)
            };
        }

        return _httpClient;
    }
}