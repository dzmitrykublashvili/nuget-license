using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using static NugetUtility.Helpers.ConsoleLogHelper;

namespace NugetUtility.Helpers;

internal class NugetHelper
{
    private const string fallbackPackageUrl = "https://www.nuget.org/api/v2/package/{0}/{1}";

    private static HttpClient _httpClient;
    private readonly PackageOptions _packageOptions;
    private readonly XmlSerializer _serializer;
    private readonly IReadOnlyDictionary<string, string> _licenseMappings;
    private static readonly Dictionary<Tuple<string, string>, string> _versionResolverCache = new();
    private static readonly Dictionary<Tuple<string, string>, Package> _requestCache = new();
    private static readonly Dictionary<Tuple<string, string>, string> _licenseFileCache = new();
    // Search nuspec in local cache (Fix for linux distro)
    private readonly string nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") 
                                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    public NugetHelper(PackageOptions packageOptions)
    {
        _httpClient = HttpClientFactory.GetHttpClient(packageOptions);
        _packageOptions = packageOptions;
        _serializer = new XmlSerializer(typeof(Package));
        _licenseMappings = packageOptions.LicenseToUrlMappingsDictionary;
    }

    /// <summary>
    /// Get Nuget References per project
    /// </summary>
    /// <param name="project">project name</param>
    /// <param name="packages">List of projects</param>
    /// <returns></returns>
    public async Task<PackageList> GetNugetInformationAsync(string project, IEnumerable<PackageNameAndVersion> packages)
    {
        var licenses = new PackageList();
        foreach (var packageWithVersion in packages)
        {
            try
            {
                if (_packageOptions.PackageFilter.Any(p => string.Compare(p, packageWithVersion.Name, StringComparison.OrdinalIgnoreCase) == 0) ||
                    _packageOptions.PackageRegex?.IsMatch(packageWithVersion.Name) == true)
                {
                    WriteOutput(packageWithVersion.Name + " skipped by filter.", logLevel: LogLevel.Verbose);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(packageWithVersion.Name) || string.IsNullOrWhiteSpace(packageWithVersion.Version))
                {
                    WriteOutput($"Skipping invalid entry {packageWithVersion.Name}, version {packageWithVersion.Version}, ", logLevel: LogLevel.Verbose);
                    continue;
                }

                var version = await ResolvePackageVersionFromLocalCacheAsync(packageWithVersion.Name, packageWithVersion.Version);

                if (!string.IsNullOrEmpty(version))
                {
                    WriteOutput($"Package '{packageWithVersion.Name}', version requirement {packageWithVersion.Version} resolved to version {version} from local cache", logLevel: LogLevel.Verbose);
                    var lookupKey = Tuple.Create(packageWithVersion.Name, version);

                    if (_requestCache.TryGetValue(lookupKey, out var package))
                    {
                        WriteOutput(packageWithVersion.Name + ", version requirement " + packageWithVersion.Version + " obtained from request cache.", logLevel: LogLevel.Information);
                        licenses.TryAdd($"{packageWithVersion.Name},{version}", package);
                        continue;
                    }

                    //Linux: package file name could be lowercase
                    var nuspecPath = CreateNuSpecPath(nugetRoot, version, packageWithVersion.Name?.ToLowerInvariant());

                    if (File.Exists(nuspecPath))
                    {
                        try
                        {
                            using var textReader = new StreamReader(nuspecPath);
                            await ReadNuspecFile(project, licenses, packageWithVersion.Name, version, lookupKey, textReader);
                            continue;
                        }
                        catch (Exception exc)
                        {
                            // Ignore errors in local cache, try online call
                            WriteOutput($"ReadNuspecFile error, package '{packageWithVersion.Name}', version {version}", exc, LogLevel.Verbose);
                        }
                    }
                    else
                    {
                        WriteOutput($"Package '{packageWithVersion.Name}', version {packageWithVersion.Version} does not contain nuspec in local cache ({nuspecPath})", logLevel: LogLevel.Error);
                    }
                }
                else
                {
                    WriteOutput($"Package '{packageWithVersion.Name}', version {packageWithVersion.Version} not found in local cache", logLevel: LogLevel.Verbose);
                }

                version = await ResolvePackageVersionFromNugetServerAsync(packageWithVersion.Name, packageWithVersion.Version);

                if (!string.IsNullOrEmpty(version))
                {
                    WriteOutput($"Package '{packageWithVersion.Name}', version requirement {packageWithVersion.Version} resolved to version {version} from NuGet server", logLevel: LogLevel.Verbose);
                    var lookupKey = Tuple.Create(packageWithVersion.Name, version);

                    if (_requestCache.TryGetValue(lookupKey, out var package))
                    {
                        WriteOutput(packageWithVersion.Name + ", version " + packageWithVersion.Version + " obtained from request cache.", logLevel: LogLevel.Information);
                        licenses.TryAdd($"{packageWithVersion.Name},{version}", package);
                        continue;
                    }

                    // Try dowload nuspec
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{packageWithVersion.Name}/{version}/{packageWithVersion.Name}.nuspec".ToLowerInvariant());
                    using var response = await _httpClient.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        WriteOutput($"{request.RequestUri} failed due to {response.StatusCode}!", logLevel: LogLevel.Warning);
                        var fallbackResult = await GetNuGetPackageFileResult<Package>(packageWithVersion.Name, version, $"{packageWithVersion.Name}.nuspec".ToLowerInvariant());
                        if (fallbackResult is Package)
                        {
                            licenses.Add($"{packageWithVersion.Name},{version}", fallbackResult);
                            await this.AddTransitivePackages(project, licenses, fallbackResult);
                            _requestCache[lookupKey] = fallbackResult;
                            await HandleLicensing(fallbackResult);
                        }
                        else
                        {
                            licenses.Add($"{packageWithVersion.Name},{version}", new Package { Metadata = new Metadata { Version = version, Id = packageWithVersion.Name } });
                        }

                        continue;
                    }

                    WriteOutput($"Successfully received {request.RequestUri}", logLevel: LogLevel.Information);
                    using (var responseText = await response.Content.ReadAsStreamAsync())
                    using (var textReader = new StreamReader(responseText))
                    {
                        try
                        {
                            await ReadNuspecFile(project, licenses, packageWithVersion.Name, version, lookupKey, textReader);
                        }
                        catch (Exception e)
                        {
                            WriteOutput(e.Message, e, LogLevel.Error);
                            throw;
                        }
                    }
                }
                else
                {
                    WriteOutput($"Package '{packageWithVersion.Name}', version {packageWithVersion.Version} not found in NuGet", logLevel: LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                WriteOutput(ex.Message, ex, LogLevel.Error);
            }
        }

        return licenses;

        static string CreateNuSpecPath(string nugetRoot, string version, string packageName)
            => Path.Combine(nugetRoot, packageName, version, $"{packageName}.nuspec");
    }

    private async Task<string> ResolvePackageVersionAsync(string name, string versionRange, Func<string, Task<IEnumerable<string>>> GetVersions)
    {
        if (_versionResolverCache.TryGetValue(Tuple.Create(name, versionRange), out string version))
        {
            return version;
        }

        var versionList = await GetVersions(name);
        version = GetVersionFromRange(versionRange, versionList.Select(v => NuGetVersion.Parse(v)));
        if (!string.IsNullOrEmpty(version))
        {
            _versionResolverCache[Tuple.Create(name, versionRange)] = version;
        }
        return version;
    }

    private async Task<string> ResolvePackageVersionFromLocalCacheAsync(string name, string versionRange)
    {
        return await ResolvePackageVersionAsync(name, versionRange, GetVersionsFromLocalCacheAsync);
    }

    private async Task<string> ResolvePackageVersionFromNugetServerAsync(string name, string versionRange)
    {
        return await ResolvePackageVersionAsync(name, versionRange, GetVersionsFromNugetServerAsync);
    }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    private async Task<IEnumerable<string>> GetVersionsFromLocalCacheAsync(string packageName)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
        // Nuget saves packages in lowercase format, and we should look for lowercase folders only to allow Linux case-sensitive folder enumeration to succeed
        DirectoryInfo di = new DirectoryInfo(Path.Combine(nugetRoot, packageName).ToLowerInvariant());
        try
        {
            return di.GetDirectories().Select(dir => dir.Name);
        }
        catch (DirectoryNotFoundException)
        {
            return Enumerable.Empty<string>();
        }

    }

    private async Task<IEnumerable<string>> GetVersionsFromNugetServerAsync(string packageName)
    {
        // Linux request fails with NotFound if URL has any uppercase letters, thus, converting it all to lowercase
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{packageName}/index.json".ToLowerInvariant());
        try
        {
            using var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                WriteOutput($"{request.RequestUri} failed due to {response.StatusCode}!", logLevel: LogLevel.Error);
                return Enumerable.Empty<string>();
            }

            var jsonData = await response.Content.ReadAsStreamAsync();
            var doc = await JsonDocument.ParseAsync(jsonData);

            if (!doc.RootElement.TryGetProperty("versions", out var versions))
            {
                WriteOutput(() => $"No \"versions\" property found in response to {request.RequestUri}", logLevel: LogLevel.Warning);
                return Enumerable.Empty<string>();
            }

            return versions.EnumerateArray().Select(v => v.GetString());
        }
        catch (HttpRequestException)
        {
            return Enumerable.Empty<string>();
        }
    }

    private string GetVersionFromRange(string versionRange, IEnumerable<NuGetVersion> versionList)
    {
        try
        {
            VersionRange vRange = VersionRange.Parse(versionRange);
            return vRange.FindBestMatch(versionList)?.ToString() ?? "";
        }
        catch (NullReferenceException)
        {
            // FindBestMatch raises NullReferenceException if versionList is empty
            return "";
        }
    }

    private async Task ReadNuspecFile(string project, PackageList licenses, string package, string version, Tuple<string, string> lookupKey, StreamReader textReader)
    {
        if (_serializer.Deserialize(new NamespaceIgnorantXmlTextReader(textReader)) is Package result)
        {
            licenses.Add($"{package},{version}", result);
            await this.AddTransitivePackages(project, licenses, result);
            _requestCache[lookupKey] = result;
            await HandleLicensing(result);
        }
    }

    private async Task AddTransitivePackages(string project, PackageList licenses, Package result)
    {
        var groups = result.Metadata?.Dependencies?.Group;
        if (_packageOptions.IncludeTransitive && groups != null && !_packageOptions.UseProjectAssetsJson)
            // project.assets.json already includes all transitive packages with the right versions, no need to re-add them
        {
            foreach (var group in groups)
            {
                var dependant =
                    group
                        .Dependency
                        .Where(e => !licenses.Keys.Contains($"{e.Id},{e.Version}"))
                        .Select(e => new PackageNameAndVersion { Name = e.Id, Version = e.Version });

                var dependantPackages = await GetNugetInformationAsync(project, dependant);
                foreach (var dependantPackage in dependantPackages)
                {
                    if (!licenses.ContainsKey(dependantPackage.Key))
                    {
                        licenses.Add(dependantPackage.Key, dependantPackage.Value);
                    }
                }
            }
        }
    }

    private async Task<T> GetNuGetPackageFileResult<T>(string packageName, string versionNumber, string fileInPackage)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(versionNumber)) { return await Task.FromResult<T>(null); }
        var fallbackEndpoint = new Uri(string.Format(fallbackPackageUrl, packageName, versionNumber).ToLowerInvariant());
        WriteOutput(() => "Attempting to download: " + fallbackEndpoint.ToString(), logLevel: LogLevel.Verbose);
        using var packageRequest = new HttpRequestMessage(HttpMethod.Get, fallbackEndpoint);
        using var packageResponse = await _httpClient.SendAsync(packageRequest, CancellationToken.None);
        if (!packageResponse.IsSuccessStatusCode)
        {
            WriteOutput($"{packageRequest.RequestUri} failed due to {packageResponse.StatusCode}!", logLevel: LogLevel.Warning);
            return null;
        }

        using var fileStream = new MemoryStream();
        await packageResponse.Content.CopyToAsync(fileStream);

        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(fileInPackage);
        if (entry is null)
        {
            WriteOutput(() => $"{fileInPackage} was not found in NuGet Package: {packageName}", logLevel: LogLevel.Verbose);
            return null;
        }
        WriteOutput(() => $"Attempting to read: {fileInPackage}", logLevel: LogLevel.Verbose);
        using var entryStream = entry.Open();
        using var textReader = new StreamReader(entryStream);
        var typeT = typeof(T);
        if (typeT == typeof(Package))
        {
            if (_serializer.Deserialize(new NamespaceIgnorantXmlTextReader(textReader)) is T result)
            {
                return (T)result;
            }
        }
        else if (typeT == typeof(string))
        {
            return await textReader.ReadToEndAsync() as T;
        }

        throw new ArgumentException($"{typeT.FullName} isn't supported!");
    }

    private async Task HandleLicensing(Package package)
    {
        if (package?.Metadata is null) { return; }
        if (package.Metadata.LicenseUrl is string licenseUrl &&
            package.Metadata.License?.Text is null)
        {
            if (_licenseMappings.TryGetValue(licenseUrl, out var mappedLicense))
            {
                package.Metadata.License = new License { Text = mappedLicense };
            }
        }

        if (!package.Metadata.License.IsLicenseFile() || _packageOptions.AllowedLicenseType.Count == 0) { return; }

        var key = Tuple.Create(package.Metadata.Id, package.Metadata.Version);

        if (_licenseFileCache.TryGetValue(key, out _)) { return; }

        _licenseFileCache[key] = await GetNuGetPackageFileResult<string>(package.Metadata.Id, package.Metadata.Version, package.Metadata.License.Text);
    }
}