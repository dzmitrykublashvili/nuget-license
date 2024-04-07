using HtmlAgilityPack;
using NugetUtility.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NugetUtility.Helpers;
using static ConsoleLogHelper;

public class LicenseHelper
{
    public const string DeprecateNugetLicenseUrl = "https://aka.ms/deprecateLicenseUrl";
    private const string fallbackPackageUrl = "https://www.nuget.org/api/v2/package/{0}/{1}";

    private readonly HttpClient _httpClient;
    private readonly PackageOptions _packageOptions;

    public LicenseHelper(PackageOptions packageOptions)
    {
        _packageOptions = packageOptions;
        _httpClient = HttpClientFactory.GetHttpClient(_packageOptions);
    }

    public ValidationResult<LibraryInfo> ValidateAllowedLicenses(List<LibraryInfo> projectPackages)
    {
        if (_packageOptions.AllowedLicenseType.Count == 0)
        {
            return new ValidationResult<LibraryInfo> { IsValid = true };
        }

        WriteOutput(() => $"Starting {nameof(ValidateAllowedLicenses)}...", logLevel: LogLevel.Verbose);

        var invalidPackages = projectPackages
            .Where(p => !_packageOptions.AllowedLicenseType.Any(allowed =>
            {
                if (p.LicenseUrl is string licenseUrl)
                {
                    if (_packageOptions.LicenseToUrlMappingsDictionary.TryGetValue(licenseUrl, out var license))
                    {
                        return allowed == license;
                    }

                    if (p.LicenseUrl?.Contains(allowed, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }
                }

                return allowed == p.LicenseType;
            }))
            .ToList();

        return new ValidationResult<LibraryInfo> { IsValid = invalidPackages.Count == 0, InvalidPackages = invalidPackages };
    }

    public ValidationResult<LibraryInfo> ValidateForbiddenLicenses(List<LibraryInfo> projectPackages)
    {
        if (_packageOptions.ForbiddenLicenseType.Count == 0)
        {
            return new ValidationResult<LibraryInfo> { IsValid = true };
        }

        WriteOutput(() => $"Starting {nameof(ValidateForbiddenLicenses)}...", logLevel: LogLevel.Verbose);

        var invalidPackages = projectPackages
            .Where(LicenseIsForbidden)
            .ToList();

        return new ValidationResult<LibraryInfo> { IsValid = invalidPackages.Count == 0, InvalidPackages = invalidPackages };

        bool LicenseIsForbidden(LibraryInfo info)
        {
            return _packageOptions.ForbiddenLicenseType.Contains(info.LicenseType);
        }
    }

    /// <summary>
    /// HandleMSFTLicenses handle deprecate MSFT nuget licenses
    /// </summary>
    /// <param name="libraries">List<LibraryInfo></param>
    /// <returns>A List of LibraryInfo</returns>
    public List<LibraryInfo> HandleDeprecateMSFTLicense(List<LibraryInfo> libraries)
    {
        List<LibraryInfo> result = libraries;

        foreach (var item in result)
        {
            if (item.LicenseUrl == DeprecateNugetLicenseUrl)
            {
                item.LicenseUrl = string.Format("https://www.nuget.org/packages/{0}/{1}/License", item.PackageName, item.PackageVersion);
            }
        }
        return result;
    }

    public async Task ExportLicenseTexts(List<LibraryInfo> infos)
    {
        var directory = GetExportDirectory(_packageOptions.OutputDirectory);

        foreach (var info in infos.Where(i => !string.IsNullOrEmpty(i.LicenseUrl)))
        {
            var source = info.LicenseUrl;
            var outpath = Path.Combine(directory, $"{info.PackageName}_{info.PackageVersion}.txt");
            var outpathhtml = Path.Combine(directory, $"{info.PackageName}_{info.PackageVersion}.html");

            if (File.Exists(outpath) || File.Exists(outpathhtml))
            {
                continue;
            }

            if (source == LicenseHelper.DeprecateNugetLicenseUrl)
            {
                if (await GetLicenceFromNpkgFile(info.PackageName, info.LicenseType, info.PackageVersion))
                    continue;
            }

            if (source == "http://go.microsoft.com/fwlink/?LinkId=329770" || source == "https://dotnet.microsoft.com/en/dotnet_library_license.htm")
            {
                if (await GetLicenceFromNpkgFile(info.PackageName, "dotnet_library_license.txt", info.PackageVersion))
                    continue;
            }

            if (source.StartsWith("https://licenses.nuget.org"))
            {
                if (await GetLicenceFromNpkgFile(info.PackageName, "License.txt", info.PackageVersion))
                    continue;
            }

            do
            {
                WriteOutput(() => $"Attempting to download {source} to {outpath}", logLevel: LogLevel.Verbose);

                // We are not using conversion of URI to lowercase here because we think that the URI provided by the package maintainer should be a correct one
                using var request = new HttpRequestMessage(HttpMethod.Get, source);

                try
                {
                    using var response = await _httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        WriteOutput($"{request.RequestUri} failed due to {response.StatusCode}!", logLevel: LogLevel.Error);
                        break;
                    }

                    // Detect a redirect 302
                    if (response.RequestMessage.RequestUri.AbsoluteUri != source)
                    {
                        WriteOutput(() => " Redirect detected", logLevel: LogLevel.Verbose);
                        source = response.RequestMessage.RequestUri.AbsoluteUri;
                        continue;
                    }

                    // Modify the URL if required
                    if (CorrectUri(source) != source)
                    {
                        WriteOutput(() => " Fixing URL", logLevel: LogLevel.Verbose);
                        source = CorrectUri(source);
                        continue;
                    }

                    // stripping away charset if exists.
                    var contentType = response.Content.Headers.GetValues("Content-Type").First().Split(';').First();

                    Stream outputStream = await response.Content.ReadAsStreamAsync();

                    if (contentType == "text/html")
                    {
                        if (!_packageOptions.ConvertHtmlToText)
                        {
                            outpath = outpathhtml;
                        }
                        else
                        {
                            outputStream = ConvertHtmlFileToText(outputStream, source);
                        }
                    }

                    await using var fileStream = File.OpenWrite(outpath);

                    await outputStream.CopyToAsync(fileStream);

                    break;
                }
                catch (HttpRequestException)
                {
                    // handled in !IsSuccessStatusCode, ignoring to continue export
                    break;
                }
                catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
                {
                    WriteOutput($"{ex.GetType().Name} during download of license url {info.LicenseUrl} exception {ex.Message}", logLevel: LogLevel.Verbose);
                    break;
                }
            } while (true);
        }
    }

    private Stream ConvertHtmlFileToText(Stream htmlStream, string sourceUrl)
    {
        var htmlDocument = new HtmlDocument();

        htmlDocument.Load(htmlStream);
        StripHtml(htmlDocument, sourceUrl, out HashSet<string> tagNamesToStrip);
        using StringWriter writer = new StringWriter();
        new HtmlPrinter(htmlDocument, writer, _packageOptions.PageWidth, tagNamesToStrip).Print();
        return new MemoryStream(Encoding.UTF8.GetBytes(writer.ToString()));
    }

    private void StripHtml(HtmlDocument htmlDocument, string sourceUrl, out HashSet<string> tagNamesToStrip)
    {
        tagNamesToStrip = new HashSet<string>();
        if (sourceUrl.Contains("://opensource.org/license/", StringComparison.InvariantCultureIgnoreCase))
        {
            HtmlNode node = htmlDocument.GetElementbyId("masthead");
            node?.Remove();

            node = htmlDocument.GetElementbyId("colophon");
            node?.Remove();
        }
        else if (sourceUrl.Contains("://www.apache.org/licenses/LICENSE-2.0", StringComparison.InvariantCultureIgnoreCase))
        {
            tagNamesToStrip.Add("header");
            tagNamesToStrip.Add("footer");
        }
    }

    /// <summary>
    /// Downloads the nuget package file and read the licence file
    /// </summary>
    /// <param name="package"></param>
    /// <param name="licenseFile"></param>
    /// <param name="version"></param>
    /// <returns></returns>
    private async Task<bool> GetLicenceFromNpkgFile(string package, string licenseFile, string version)
    {
        bool result = false;
        var nupkgEndpoint = new Uri(string.Format(fallbackPackageUrl, package, version).ToLowerInvariant());
        WriteOutput(() => $"Attempting to download: {nupkgEndpoint}", logLevel: LogLevel.Verbose);
        using var packageRequest = new HttpRequestMessage(HttpMethod.Get, nupkgEndpoint);
        using var packageResponse = await _httpClient.SendAsync(packageRequest, CancellationToken.None);

        if (!packageResponse.IsSuccessStatusCode)
        {
            WriteOutput($"{packageRequest.RequestUri} failed due to {packageResponse.StatusCode}!", logLevel: LogLevel.Warning);
            return false;
        }

        var directory = GetExportDirectory(_packageOptions.OutputDirectory);
        var outpath = Path.Combine(directory, $"{package}_{version}.nupkg.zip");

        await using (var fileStream = File.OpenWrite(outpath))
        {
            try
            {
                await packageResponse.Content.CopyToAsync(fileStream);
            }
            catch (Exception)
            {
                return false;
            }
        }

        using (ZipArchive archive = ZipFile.OpenRead(outpath))
        {
            var sample = archive.GetEntry(licenseFile);
            if (sample != null)
            {
                var t = sample.Open();
                if (t != null && t.CanRead)
                {
                    var libTxt = outpath.Replace(".nupkg.zip", ".txt");
                    using var fileStream = File.OpenWrite(libTxt);
                    try
                    {
                        await t.CopyToAsync(fileStream);
                        result = true;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            }
        }

        File.Delete(outpath);
        return result;
    }

    /// <summary>
    /// make the appropriate changes to the URI to get the raw text of the license.
    /// </summary>
    /// <param name="uri">URI</param>
    /// <returns>Returns the raw URL to get the raw text of the library</returns>
    private string CorrectUri(string uri)
    {
        if (!IsGithub(uri))
        {
            return uri;
        }

        if (uri.Contains("/blob/", StringComparison.Ordinal))
        {
            uri = uri.Replace("/blob/", "/raw/", StringComparison.Ordinal);
        }

        /*  if (uri.Contains("/dotnet/corefx/", StringComparison.Ordinal))
          {
              uri = uri.Replace("/dotnet/corefx/", "/dotnet/runtime/", StringComparison.Ordinal);
          }*/

        return uri;
    }

    private bool IsGithub(string uri)
    {
        return uri.StartsWith("https://github.com", StringComparison.Ordinal);
    }

    public static string GetExportDirectory(string outputDirectorySetting)
    {
        string outputDirectory = string.Empty;

        if (!string.IsNullOrWhiteSpace(outputDirectorySetting))
        {
            if (outputDirectorySetting.EndsWith('/'))
            {
                outputDirectory = Path.GetDirectoryName(outputDirectorySetting);
            }
            else
            {
                outputDirectory = Path.GetDirectoryName(outputDirectorySetting + "/");

            }

            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
        }

        outputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? Environment.CurrentDirectory : outputDirectory;
        return outputDirectory;
    }

}