using System;
using System.Collections.Generic;
using System.Linq;
using NugetUtility.Models;

namespace NugetUtility.Helpers;

internal static class ConsoleLogHelper
{
    public static LogLevel LogLevelThreshold { get; set; } = LogLevel.None;

    public static void WriteOutput(Func<string> line, Exception exception = null, LogLevel logLevel = LogLevel.Information)
    {
        if ((int)logLevel < (int)LogLevelThreshold)
        {
            return;
        }

        Console.WriteLine(line.Invoke());

        if (exception is not null)
        {
            Console.WriteLine(exception.ToString());
        }
    }

    public static void WriteOutput(string line, Exception exception = null, LogLevel logLevel = LogLevel.Information) => 
        WriteOutput(() => line, exception, logLevel);

    public static void PrintLicenses(List<LibraryInfo> libraries)
    {
        if (libraries is null) { throw new ArgumentNullException(nameof(libraries)); }
        if (!libraries.Any()) { return; }

        WriteOutput(Environment.NewLine + "References:", logLevel: LogLevel.Always);
        WriteOutput(libraries.ToStringTable(new[] { "Reference", "Version", "License Type", "License" }, false,
            a => a.PackageName ?? "---",
            a => a.PackageVersion ?? "---",
            a => a.LicenseType ?? "---",
            a => a.LicenseUrl ?? "---"), logLevel: LogLevel.Always);
    }
}