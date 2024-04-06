using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NugetUtility.Helpers;
using static NugetUtility.Helpers.ConsoleLogHelper;

namespace NugetUtility;

public class Extractor
{
    public const string NugetUrl = "https://api.nuget.org/v3-flatcontainer/";

    private readonly IReadOnlyDictionary<string, string> _licenseMappings;
    private readonly PackageOptions _packageOptions;
    private readonly NugetHelper _nugetHelper;
    private Dictionary<string, string> _solutionProjects = new();

    public Extractor(PackageOptions packageOptions, HttpClient httpClient = null)
    {
        _packageOptions = packageOptions;
        _licenseMappings = packageOptions.LicenseToUrlMappingsDictionary;
        LogLevelThreshold = _packageOptions.LogLevelThreshold;
        ProjectHelper.PackageOptions = _packageOptions;
        _nugetHelper = new NugetHelper(packageOptions);
    }

    public async Task<List<LibraryInfo>> GetPackages()
    {
        if (_packageOptions.SolutionFilePath is not null)
        {
            _solutionProjects = await SolutionHelper.FillSolutionProjects(_packageOptions.SolutionFilePath);
        }

        WriteOutput(() => $"Starting {nameof(GetPackages)}...", logLevel: LogLevel.Verbose);

        var packages = new Dictionary<string, PackageList>();

        IEnumerable<string> projectFiles = await ProjectHelper.GetValidProjects(_packageOptions.ProjectDirectory, _solutionProjects);

        foreach (var projectFile in projectFiles)
        {
            var references = ProjectHelper.GetLibraryReferencesFromProject(projectFile);
            var referencedPackages = references.Select((package) =>
            {
                var split = package.Split(',', 2);
                return new PackageNameAndVersion { Name = split[0], Version = split[1] };
            });

            WriteOutput(Environment.NewLine + "Project:" + projectFile + Environment.NewLine, logLevel: LogLevel.Information);
            var currentProjectLicenses = await _nugetHelper.GetNugetInformationAsync(projectFile, referencedPackages);
            packages[projectFile] = currentProjectLicenses;
        }

        var libraries = MapPackagesToLibraryInfo(packages);

        return libraries;
    }

    public static bool IgnoreSslCertificateErrorCallback(
        HttpRequestMessage message, 
        System.Security.Cryptography.X509Certificates.X509Certificate2 cert, 
        System.Security.Cryptography.X509Certificates.X509Chain chain, 
        System.Net.Security.SslPolicyErrors sslPolicyErrors)
        => true;

    /// <summary>
    /// Main function to cleanup
    /// </summary>
    /// <param name="packages"></param>
    /// <returns></returns>
    private List<LibraryInfo> MapPackagesToLibraryInfo(Dictionary<string, PackageList> packages)
    {
        WriteOutput(() => $"Starting {nameof(MapPackagesToLibraryInfo)}...", logLevel: LogLevel.Verbose);
        var libraryInfos = new List<LibraryInfo>(256);

        foreach (var packageList in packages)
        {
            foreach (var item in packageList.Value.Select(p => p.Value))
            {
                var info = MapPackageToLibraryInfo(item, packageList.Key);
                libraryInfos.Add(info);
            }
        }

        // merge in missing manual items where there wasn't a package
        var missedManualItems = _packageOptions.ManualInformation.Except(libraryInfos, LibraryNameAndVersionComparer.Default);

        foreach (var missed in missedManualItems)
        {
            libraryInfos.Add(missed);
        }

        if (_packageOptions.UniqueOnly)
        {
            libraryInfos = libraryInfos
                .GroupBy(x => new { x.PackageName, x.PackageVersion })
                .Select(g =>
                {
                    var first = g.First();
                    return new LibraryInfo
                    {
                        PackageName = first.PackageName,
                        PackageVersion = first.PackageVersion,
                        PackageUrl = first.PackageUrl,
                        Copyright = first.Copyright,
                        Authors = first.Authors,
                        Description = first.Description,
                        LicenseType = first.LicenseType,
                        LicenseUrl = first.LicenseUrl,
                        Projects = _packageOptions.IncludeProjectFile ? string.Join(";", g.Select(p => p.Projects)) : null
                    };
                })
                .ToList();
        }

        return libraryInfos
            .OrderBy(p => p.PackageName)
            .ToList();
    }

    private LibraryInfo MapPackageToLibraryInfo(Package item, string projectFile)
    {
        string licenseType = item.Metadata.License?.Text ?? null;
        string licenseUrl = item.Metadata.LicenseUrl ?? null;

        if (licenseUrl is string && string.IsNullOrWhiteSpace(licenseType))
        {
            if (_licenseMappings.TryGetValue(licenseUrl, out var license))
            {
                licenseType = license;
            }
        }

        var manual = _packageOptions.ManualInformation
            .FirstOrDefault(f => f.PackageName == item.Metadata.Id && f.PackageVersion == item.Metadata.Version);

        return new LibraryInfo
        {
            PackageName = item.Metadata.Id ?? string.Empty,
            PackageVersion = item.Metadata.Version ?? string.Empty,
            PackageUrl = !string.IsNullOrWhiteSpace(manual?.PackageUrl) ?
                manual.PackageUrl :
                item.Metadata.ProjectUrl ?? string.Empty,
            Copyright = item.Metadata.Copyright ?? string.Empty,
            Authors = manual?.Authors ?? item.Metadata.Authors?.Split(',') ?? new string[] { },
            Description = !string.IsNullOrWhiteSpace(manual?.Description) ?
                manual.Description :
                item.Metadata.Description ?? string.Empty,
            LicenseType = manual?.LicenseType ?? licenseType ?? string.Empty,
            LicenseUrl = manual?.LicenseUrl ?? licenseUrl ?? string.Empty,
            Projects = _packageOptions.IncludeProjectFile ? projectFile : null,
            Repository = manual?.Repository ?? new LibraryRepositoryInfo
            {
                Url = item.Metadata.Repository?.Url ?? string.Empty,
                Commit = item.Metadata.Repository?.Commit ?? string.Empty,
                Type = item.Metadata.Repository?.Type ?? string.Empty,
            }
        };
    }
}