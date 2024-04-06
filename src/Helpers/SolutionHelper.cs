using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NugetUtility.Helpers;

internal static class SolutionHelper
{
    public static async Task<Dictionary<string, string>> FillSolutionProjects(string slnPath)
    {
        var sln = await File.ReadAllTextAsync(slnPath);
        var regexProj = new Regex(@"Project\(""{.*}""\) = ""(.*)"", ""(.*\.csproj)"",");
        var groups = regexProj.Matches(sln).Select(m => m.Groups);

        var slnFolderPath = Path.GetDirectoryName(slnPath);

        var solutionProjects = new Dictionary<string, string>();

        foreach (var g in groups)
        {
            var projFileName = $"{g[1].Value}{Path.GetExtension(g[2].Value)}";
            var projFilePath = $"{slnFolderPath}\\{g[2].Value}";
            solutionProjects[projFileName] = projFilePath;

            if (!File.Exists(projFilePath))
            {
                solutionProjects.Remove(projFileName);
            }
        }

        return solutionProjects;
    }

    public static async Task<IEnumerable<string>> ParseSolution(string fullName)
    {
        var solutionFile = new FileInfo(fullName);
        if (!solutionFile.Exists) { throw new FileNotFoundException(fullName); }
        var projectFiles = new List<string>(250);

        using (var fileStream = solutionFile.OpenRead())
        using (var streamReader = new StreamReader(fileStream))
        {
            while (await streamReader.ReadLineAsync() is string line)
            {
                if (!line.StartsWith("Project")) { continue; }
                var segments = line.Split(',');
                if (segments.Length < 2) { continue; }
                projectFiles.Add(segments[1].EnsureCorrectPathCharacter().Trim('"'));
            }
        }

        return projectFiles;
    }
}