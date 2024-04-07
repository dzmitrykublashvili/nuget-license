using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NugetUtility.Exceptions;
using NugetUtility.Helpers;
using NugetUtility.Models;

namespace NugetUtility;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        var result = Parser.Default.ParseArguments<PackageOptions>(args);

        return await result.MapResult(Execute, errors => Task.FromResult(1));
    }

    private static async Task<int> Execute(PackageOptions options)
    {
        if (!ValidateOptions(options))
        {
            return 1;
        }

        try
        {
            var libraries = await new Extractor(options).GetPackages();

            libraries = await ProcessLicenses(libraries, options);

            if (options.Print == true)
            {
                Console.WriteLine();
                Console.WriteLine($"Collected licenses count is: {libraries.Count}");
                Console.WriteLine("Project Reference(s) Analysis...");
                ConsoleLogHelper.PrintLicenses(libraries);
            }

            var saverHelper = new FileSystemHelper(options);

            if (File.Exists(options.MergeJsonFilePath))
            {
                libraries = await FileSystemHelper.MergeLibrariesWithLibrariesFromJsonFile(libraries,
                    options.MergeJsonFilePath);
            }

            if (options.JsonOutput)
            {
                await saverHelper.SaveAsJson(libraries);
            }
            else if (options.MarkDownOutput)
            {
                await saverHelper.SaveAsMarkdown(libraries);
            }
            else
            {
                await saverHelper.SaveAsTextFile(libraries);
            }

            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return -1;
        }
    }

    private static async Task<List<LibraryInfo>> ProcessLicenses(List<LibraryInfo> libraries, PackageOptions options)
    {
        var licenseHelper = new LicenseHelper(options);

        var validationResult = options.AllowedLicenseType.Any()
            ? licenseHelper.ValidateAllowedLicenses(libraries)
            : licenseHelper.ValidateForbiddenLicenses(libraries);

        if (!validationResult.IsValid)
        {
            throw new InvalidLicensesException<LibraryInfo>(validationResult, options);
        }

        if (options.ExportLicenseTexts)
        {
            await licenseHelper.ExportLicenseTexts(libraries);
        }

        return licenseHelper.HandleDeprecateMSFTLicense(libraries);
    }

    private static bool ValidateOptions(PackageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectDirectory))
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("-i\tInput the Directory Path (csproj or fsproj file)");

            return false;
        }

        if (options.ConvertHtmlToText && !options.ExportLicenseTexts)
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("--convert-html-to-text\tThis option requires the --export-license-texts option.");

            return false;
        }

        if (options.ForbiddenLicenseType.Any() && options.AllowedLicenseType.Any())
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("--allowed-license-types\tCannot be used with the --forbidden-license-types option.");

            return false;
        }

        if (options.UseProjectAssetsJson && !options.IncludeTransitive)
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("--use-project-assets-json\tThis option always includes transitive references, so you must also provide the -t option.");

            return false;
        }

        if (options.Timeout < 1)
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("--timeout\tThe timeout must be a positive number.");

            return false;
        }

        return true;
    }
}