using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NugetUtility.Helpers;

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
        if (string.IsNullOrWhiteSpace(options.ProjectDirectory))
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("-i\tInput the Directory Path (csproj or fsproj file)");

            return 1;
        }

        if (options.ConvertHtmlToText && !options.ExportLicenseTexts)
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("--convert-html-to-text\tThis option requires the --export-license-texts option.");

            return 1;
        }

        if (options.ForbiddenLicenseType.Any() && options.AllowedLicenseType.Any())
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("--allowed-license-types\tCannot be used with the --forbidden-license-types option.");

            return 1;
        }

        if (options.UseProjectAssetsJson && !options.IncludeTransitive)
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("--use-project-assets-json\tThis option always includes transitive references, so you must also provide the -t option.");

            return 1;
        }

        if (options.Timeout < 1)
        {
            Console.WriteLine("ERROR(S):");
            Console.WriteLine("--timeout\tThe timeout must be a positive number.");

            return 1;
        }

        try
        {
            var extractor = new Extractor(options);
            var libraries = await extractor.GetPackages();

            var licenseHelper = new LicenseHelper(options);
            HandleInvalidLicenses(libraries, options, licenseHelper);

            if (options.ExportLicenseTexts)
            {
                await licenseHelper.ExportLicenseTexts(libraries);
            }

            libraries = licenseHelper.HandleDeprecateMSFTLicense(libraries);

            if (options.Print == true)
            {
                Console.WriteLine();
                Console.WriteLine($"Collected licenses count is: {libraries.Count}");
                Console.WriteLine("Project Reference(s) Analysis...");
                ConsoleLogHelper.PrintLicenses(libraries);
            }

            var saverHelper = new SaverHelper(options);

            if (options.JsonOutput)
            {
                saverHelper.SaveAsJson(libraries);
            }
            else if (options.MarkDownOutput)
            {
                saverHelper.SaveAsMarkdown(libraries);
            }
            else
            {
                saverHelper.SaveAsTextFile(libraries);
            }

            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return -1;
        }
    }

    private static void HandleInvalidLicenses(List<LibraryInfo> libraries, PackageOptions options, LicenseHelper licenseHelper)
    {
        var invalidPackages = options.AllowedLicenseType.Any()
            ? licenseHelper.ValidateAllowedLicenses(libraries)
            : licenseHelper.ValidateForbiddenLicenses(libraries);

        if (!invalidPackages.IsValid)
        {
            throw new InvalidLicensesException<LibraryInfo>(invalidPackages, options);
        }
    }
}