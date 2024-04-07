using Newtonsoft.Json;
using NugetUtility.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static NugetUtility.Helpers.ConsoleLogHelper;

namespace NugetUtility.Helpers;

internal class ResultsExporter
{
    private readonly PackageOptions _packageOptions;
    private List<LibraryInfo> _libraries;


    public ResultsExporter(PackageOptions packageOptions, List<LibraryInfo> libraries)
    {
        _packageOptions = packageOptions;
        _libraries = libraries;
    }

    public async Task ExportResults()
    {
        if (!_libraries.Any())
        {
            Console.WriteLine("No libraries extracted.");
            return;
        }

        if (_packageOptions.Print == true)
        {
            Console.WriteLine();
            Console.WriteLine($"Collected licenses count is: {_libraries.Count}");
            Console.WriteLine("Project Reference(s) Analysis...");
            PrintLicenses(_libraries);
        }

        if (File.Exists(_packageOptions.MergeJsonFilePath))
        {
            _libraries = await MergeLibrariesWithLibrariesFromJsonFile(_libraries, _packageOptions.MergeJsonFilePath);
        }

        if (_packageOptions.JsonOutput)
        {
            await SaveAsJson(_libraries);
        }

        if (_packageOptions.MarkDownOutput)
        {
            await SaveAsMarkdown(_libraries);
        }

        if (_packageOptions.TextOutput)
        {
            await SaveAsTextFile(_libraries);
        }
    }

    public static async Task<List<LibraryInfo>> MergeLibrariesWithLibrariesFromJsonFile(List<LibraryInfo> libraries, string jsonFilePath)
    {
        var str = await File.ReadAllTextAsync(jsonFilePath);
        var libsToMerge = JsonConvert.DeserializeObject(str, typeof(List<LibraryInfo>)) as List<LibraryInfo>;

        libsToMerge?.ForEach(l =>
        {
            if (!libraries.Any(presentedLib => presentedLib.PackageName == l.PackageName && presentedLib.PackageVersion == l.PackageVersion))
            {
                libraries.Add(l);
            }
        });

        libraries = libraries.OrderBy(l => l.PackageName).ToList();

        return libraries;
    }

    public async Task SaveAsJson(List<LibraryInfo> libraries)
    {
        if (!libraries.Any())
        {
            return;
        }

        var jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = _packageOptions.IncludeProjectFile ? NullValueHandling.Include : NullValueHandling.Ignore,
            Formatting = Formatting.Indented
        };

        await using var fileStream = new FileStream(GetOutputFilename("licenses.json"), FileMode.Create);
        await using var streamWriter = new StreamWriter(fileStream);

        await streamWriter.WriteAsync(JsonConvert.SerializeObject(libraries, typeof(List<LibraryInfo>), jsonSettings));
        await streamWriter.FlushAsync();
    }

    public async Task SaveAsTextFile(List<LibraryInfo> libraries)
    {
        if (!libraries.Any())
        {
            return;
        }

        var sb = new StringBuilder(256);

        foreach (var lib in libraries)
        {
            sb.Append(new string('#', 100));
            sb.AppendLine();
            sb.Append("Package:");
            sb.Append(lib.PackageName);
            sb.AppendLine();
            sb.Append("Version:");
            sb.Append(lib.PackageVersion);
            sb.AppendLine();
            sb.Append("project URL:");
            sb.Append(lib.PackageUrl);
            sb.AppendLine();
            sb.Append("Description:");
            sb.Append(lib.Description);
            sb.AppendLine();
            sb.Append("licenseUrl:");
            sb.Append(lib.LicenseUrl);
            sb.AppendLine();
            sb.Append("license Type:");
            sb.Append(lib.LicenseType);
            sb.AppendLine();
            sb.Append("Copyright:");
            sb.Append(lib.Copyright);
            sb.AppendLine();
            if (_packageOptions.IncludeProjectFile)
            {
                sb.Append("Project:");
                sb.Append(lib.Projects);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        var filePath = GetOutputFilename("licenses.txt");
        Console.WriteLine($"Saving results to the file {filePath}...");
        await File.WriteAllTextAsync(filePath, sb.ToString());
    }

    public async Task SaveAsMarkdown(List<LibraryInfo> libraries)
    {
        if (!libraries.Any())
        {
            return;
        }

        WriteOutput(Environment.NewLine + "References:", logLevel: LogLevel.Always);

        var output = (libraries.ToStringTable(["Reference", "Version", "License Type", "License"], true,
            a => a.PackageName ?? "---",
            a => a.PackageVersion ?? "---",
            a => a.LicenseType ?? "---",
            a => a.LicenseUrl ?? "---"), logLevel: LogLevel.Always);

        await File.WriteAllTextAsync(GetOutputFilename("licenses.md"), output.Item1);
    }

    private string GetExportDirectory()
    {
        var outputDirectory = string.Empty;

        if (!string.IsNullOrWhiteSpace(_packageOptions.OutputDirectory))
        {
            outputDirectory = _packageOptions.OutputDirectory.EndsWith('/') 
                ? Path.GetDirectoryName(_packageOptions.OutputDirectory) 
                : Path.GetDirectoryName(_packageOptions.OutputDirectory + "/");

            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
        }

        outputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? Environment.CurrentDirectory : outputDirectory;
        return outputDirectory;
    }

    private string GetOutputFilename(string defaultName)
    {
        var outputDir = GetExportDirectory();

        return string.IsNullOrWhiteSpace(_packageOptions.OutputFileName) 
            ? Path.Combine(outputDir, defaultName) 
            : Path.Combine(outputDir, _packageOptions.OutputFileName);
    }
}