using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static NugetUtility.Helpers.ConsoleLogHelper;

namespace NugetUtility.Helpers;

internal class SaverHelper
{
    private readonly PackageOptions _packageOptions;

    public SaverHelper(PackageOptions packageOptions)
    {
        _packageOptions = packageOptions;
    }

    public void SaveAsJson(List<LibraryInfo> libraries)
    {
        if (!libraries.Any())
        {
            return;
        }

        JsonSerializerSettings jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = _packageOptions.IncludeProjectFile ? NullValueHandling.Include : NullValueHandling.Ignore
        };

        using var fileStream = new FileStream(GetOutputFilename("licenses.json"), FileMode.Create);
        using var streamWriter = new StreamWriter(fileStream);
        streamWriter.Write(JsonConvert.SerializeObject(libraries, jsonSettings));
        streamWriter.Flush();
    }

    public void SaveAsTextFile(List<LibraryInfo> libraries)
    {
        if (!libraries.Any() || !_packageOptions.TextOutput) { return; }
        StringBuilder sb = new StringBuilder(256);
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
        File.WriteAllText(filePath, sb.ToString());
    }

    public void SaveAsMarkdown(List<LibraryInfo> libraries)
    {
        if (libraries is null) { throw new ArgumentNullException(nameof(libraries)); }
        if (!libraries.Any()) { return; }

        WriteOutput(Environment.NewLine + "References:", logLevel: LogLevel.Always);
        var output = (libraries.ToStringTable(new[] { "Reference", "Version", "License Type", "License" }, true,
            a => a.PackageName ?? "---",
            a => a.PackageVersion ?? "---",
            a => a.LicenseType ?? "---",
            a => a.LicenseUrl ?? "---"), logLevel: LogLevel.Always);

        File.WriteAllText(GetOutputFilename("licenses.md"), output.Item1);
    }

    public string GetExportDirectory()
    {
        string outputDirectory = string.Empty;

        if (!string.IsNullOrWhiteSpace(_packageOptions.OutputDirectory))
        {
            if (_packageOptions.OutputDirectory.EndsWith('/'))
            {
                outputDirectory = Path.GetDirectoryName(_packageOptions.OutputDirectory);
            }
            else
            {
                outputDirectory = Path.GetDirectoryName(_packageOptions.OutputDirectory + "/");

            }

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
        string outputDir = GetExportDirectory();

        return string.IsNullOrWhiteSpace(_packageOptions.OutputFileName) ?
            Path.Combine(outputDir, defaultName) :
            Path.Combine(outputDir, _packageOptions.OutputFileName);
    }
}