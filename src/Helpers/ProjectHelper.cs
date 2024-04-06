using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.XPath;
using static NugetUtility.Helpers.ConsoleLogHelper;
using static NugetUtility.Utilities;

namespace NugetUtility.Helpers;

internal static class ProjectHelper
{
    public static PackageOptions PackageOptions;

    public static string[] GetProjectExtensions(bool withWildcard = false) =>
        withWildcard ?
            new[] { "*.csproj", "*.fsproj", "*.vbproj" } :
            new[] { ".csproj", ".fsproj", ".vbproj" };

    /// <summary>
    /// Retrieves the paths to referenced projects from csproj file format
    /// </summary>
    /// <param name="projectPath">The Project Path</param>
    /// <param name="solutionProjects">Filled project's dictionary.</param>
    /// <returns></returns>
    public static IEnumerable<string> GetReferencedProjectsPathsFromProjectFile(string projectPath, Dictionary<string, string> solutionProjects)
    {
        var projectsPathsCollected = new HashSet<string>();

        RecursionFillReferencedProjects(solutionProjects[projectPath]);

        void RecursionFillReferencedProjects(string projectPath)
        {
            var projDefinition = XDocument.Load(projectPath);

            var projectsNames = projDefinition
                .XPathSelectElements("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='ProjectReference']")?
                .Select(GetProjectName).ToArray();

            var projectsPaths = projectsNames.Select(pn => solutionProjects[pn]).ToHashSet();

            foreach (var projPath in projectsPaths)
            {
                var isNotExists = projectsPathsCollected.Add(projPath);

                // If project path is already exists in collection - do not make recursive call to prevent circular reference
                if (isNotExists)
                {
                    RecursionFillReferencedProjects(projPath);
                }
            }
        }

        projectsPathsCollected.Add(solutionProjects[projectPath]);

        return projectsPathsCollected;
    }

    public static string GetProjectReferenceFromElement(XElement refElem)
    {
        string version, package = refElem.Attribute("Include")?.Value ?? string.Empty;

        var versionAttribute = refElem.Attribute("Version");

        if (versionAttribute is not null)
        {
            version = versionAttribute.Value;
        }
        else // no version attribute, look for child element
        {
            version = refElem.Elements()
                .FirstOrDefault(elem => elem.Name.LocalName == "Version")?.Value ?? string.Empty;
        }

        return $"{package},{version}";
    }

    /// <summary>
    /// Retrieves the library references from csproj or fsproj file
    /// </summary>
    /// <param name="projectPath">The Project Path</param>
    /// <returns></returns>
    public static IEnumerable<string> GetLibraryReferencesFromProject(string projectPath)
    {
        WriteOutput(() => $"Starting {nameof(GetLibraryReferencesFromProject)}...", logLevel: LogLevel.Verbose);

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentNullException(projectPath);
        }

        //if (!GetProjectExtensions().Any(projExt => projectPath.EndsWith(projExt)))
        //{
        //    projectPath = GetValidProjects(projectPath).GetAwaiter().GetResult().FirstOrDefault();
        //}

        if (projectPath is null)
        {
            throw new FileNotFoundException();
        }

        IEnumerable<string> references = Array.Empty<string>();

        // First use project.assets.json, if this option is enabled.
        if (PackageOptions.UseProjectAssetsJson)
        {
            var assetsFile = Path.Combine(Path.GetDirectoryName(projectPath) ?? ".", "obj", "project.assets.json");
            if (!File.Exists(assetsFile))
            {
                WriteOutput(() => $"Cannot find {assetsFile}", logLevel: LogLevel.Warning);
            }
            else
            {
                references = GetProjectReferencesFromAssetsFile(assetsFile);
            }
        }

        // Then try to get references from new project file format
        if (!references.Any())
        {
            references = GetLibraryReferencesFromNewProjectFile(projectPath).ToArray();
        }

        // Then if needed from old packages.config
        if (!references.Any())
        {
            references = GetProjectReferencesFromPackagesConfig(projectPath);
        }

        return references ?? Array.Empty<string>();
    }

    public static async Task<IEnumerable<string>> GetValidProjects(string projectPath, Dictionary<string, string> solutionProjects)
    {
        var pathInfo = new FileInfo(projectPath);
        var extensions = GetProjectExtensions();
        List<string> validProjects;
        switch (pathInfo.Extension)
        {
            case ".sln":
                validProjects = (await SolutionHelper.ParseSolution(pathInfo.FullName))
                    .Select(p => new FileInfo(Path.Combine(pathInfo.Directory.FullName, p)))
                    .Where(p => p.Exists && extensions.Contains(p.Extension))
                    .Select(p => p.FullName).ToList();
                break;
            case ".csproj":
                validProjects = GetReferencedProjectsPathsFromProjectFile(projectPath, solutionProjects).ToList();
                break;
            //case ".fsproj":
            //    validProjects = new List<string>() { projectPath };
            //    break;
            //case ".vbproj":
            //    validProjects = new List<string>() { projectPath };
            //    break;
            case ".json":
                validProjects = ReadListFromFile<string>(projectPath)
                    .Select(x => x.EnsureCorrectPathCharacter())
                    .ToList();
                break;
            default:
                throw new InvalidOperationException(
                    "Unsupported project path. You need to specify project name like that: '-i projectName.csproj'");
                //var proj =
                //    GetProjectExtensions(withWildcard: true)
                //    .SelectMany(wildcardExtension =>
                //       Directory.EnumerateFiles(projectPath, wildcardExtension, SearchOption.AllDirectories)
                //    ).First();

                //validProjects = GetReferencedProjectsPathsFromNewProjectFile(proj).ToList();

                break;
        }

        WriteOutput(() => $"Discovered Project Files {Environment.NewLine}", logLevel: LogLevel.Information);
        WriteOutput(() => string.Join(Environment.NewLine, validProjects.ToArray()), logLevel: LogLevel.Information);

        return GetFilteredProjects(validProjects);
    }

    /// <summary>
    /// Gets project references from a project.assets.json file.
    /// </summary>
    /// <param name="assetsPath">The assets file</param>
    /// <returns></returns>
    private static IEnumerable<string> GetProjectReferencesFromAssetsFile(string assetsPath)
    {
        WriteOutput(() => $"Reading assets file {assetsPath}", logLevel: LogLevel.Verbose);
        using var assetsFileStream = File.OpenRead(assetsPath);
        var doc = JsonDocument.Parse(assetsFileStream);
        assetsFileStream.Dispose();

        if (!doc.RootElement.TryGetProperty("targets", out var targets))
        {
            WriteOutput(() => $"No \"targets\" property found in {assetsPath}", logLevel: LogLevel.Warning);
            yield break;
        }

        foreach (var target in targets.EnumerateObject())
        {
            WriteOutput(() => $"Reading dependencies for target {target.Name}", logLevel: LogLevel.Verbose);
            foreach (var dep in target.Value.EnumerateObject())
            {
                var depName = dep.Name.Split('/');
                if (depName.Length != 2)
                {
                    WriteOutput(() => $"Unexpected package name: {dep.Name}", logLevel: LogLevel.Warning);
                    continue;
                }

                if (dep.Value.TryGetProperty("type", out var type) && type.ValueEquals("package"))
                {
                    yield return string.Join(",", depName);
                }
            }
        }
    }

    /// <summary>
    /// Retreive the lib references from new csproj file format
    /// </summary>
    /// <param name="projectPath">The Project Path</param>
    /// <returns></returns>
    private static IEnumerable<string> GetLibraryReferencesFromNewProjectFile(string projectPath)
    {
        var projDefinition = XDocument.Load(projectPath);

        // Uses an XPath instead of direct navigation (using Elements("…")) as the project file may use xml namespaces
        return projDefinition?
                   .XPathSelectElements("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='PackageReference']")?
                   .Select(ProjectHelper.GetProjectReferenceFromElement);
    }

    /// <summary>
    /// Retrieve the project references from old packages.config file
    /// </summary>
    /// <param name="projectPath">The Project Path</param>
    /// <returns></returns>
    private static IEnumerable<string> GetProjectReferencesFromPackagesConfig(string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath);
        var packagesFile = Path.Join(dir, "packages.config");

        if (File.Exists(packagesFile))
        {
            var packagesConfig = XDocument.Load(packagesFile);

            return packagesConfig?
                .Element("packages")?
                .Elements("package")?
                .Select(refElem => (refElem.Attribute("id")?.Value ?? string.Empty) + "," + (refElem.Attribute("version")?.Value ?? string.Empty));
        }

        return Array.Empty<string>();
    }

    private static IEnumerable<string> GetFilteredProjects(IEnumerable<string> projects)
    {
        if (PackageOptions.ProjectFilter.Count == 0)
        {
            return projects;
        }

        var filteredProjects = projects.Where(project => !PackageOptions.ProjectFilter
            .Any(projectToSkip =>
                project.Contains(projectToSkip, StringComparison.OrdinalIgnoreCase)
            )).ToList();

        WriteOutput(() => $"Filtered Project Files {Environment.NewLine}", logLevel: LogLevel.Verbose);
        WriteOutput(() => string.Join(Environment.NewLine, filteredProjects.ToArray()), logLevel: LogLevel.Verbose);

        return filteredProjects;
    }

    private static string GetProjectName(XElement refElem)
    {
        var relativePath = refElem.Attribute("Include")?.Value ?? string.Empty;
        var lastIndOfSlash = relativePath.LastIndexOf('\\');
        var name = relativePath[(lastIndOfSlash + 1)..];

        return name;
    }
}