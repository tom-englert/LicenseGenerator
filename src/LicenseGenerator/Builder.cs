using System.Text;
using System.Text.RegularExpressions;

using LicenseGenerator;

using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

using TomsToolbox.Essentials;

using static Constants;

internal sealed class Builder
{
    private static readonly string Delimiter = new('-', 80);

    private readonly ProjectInfo[] _projects;
    private readonly Dictionary<string, ProjectInfo> _includedProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _outputPath;
    private readonly string _solutionDirectory;
    private readonly Regex? _excludeRegex;

    public Builder(FileInfo input, string output, string? exclude)
    {
        _outputPath = output;
        _solutionDirectory = input.DirectoryName ?? ".";
        _excludeRegex = string.IsNullOrEmpty(exclude) ? null : new Regex(exclude, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        if (string.IsNullOrEmpty(Path.GetDirectoryName(_outputPath)))
        {
            _outputPath = Path.Combine(_solutionDirectory, _outputPath);
        }

        Output.WriteLine($"Solution: '{input}'");
        Output.WriteLine();

        var solution = SolutionFile.Parse(input.FullName);

        _projects = LoadProjects(solution)
            .ExceptNullItems()
            .ToArray();

        Output.WriteLine();
    }

    public async Task<int> Build()
    {
        foreach (var project in _projects)
        {
            if (!ShouldInclude(project))
                continue;

            Output.WriteLine($"Include: {project.ProjectReference.RelativePath}");

            AddProject(project);
        }

        if (!_includedProjects.Any())
        {
            Output.WriteError("No projects to include, filter not generated");
            return 1;
        }

        Output.WriteLine();

        var packages = await LoadPackages();

        Output.WriteLine();
        Output.WriteLine($"Create: '{_outputPath}'");

        await CreateLicenseFile(packages);

        return 0;
    }

    private async Task<ICollection<PackageArchiveReader>> LoadPackages()
    {
        var packageIdentities = _includedProjects.Values
            .SelectMany(info => info.Project.AllEvaluatedItems.Where(item => item.ItemType == "PackageReference"))
            .Where(item => item.GetMetadata("PrivateAssets") == null)
            .Select(item => new { Id = item.EvaluatedInclude, Version = item.GetMetadata("Version")?.EvaluatedValue })
            .Where(item => !string.IsNullOrEmpty(item.Version))
            .Select(item => new PackageIdentity(item.Id, NuGetVersion.Parse(item.Version!)))
            .Distinct(new DelegateEqualityComparer<PackageIdentity>(item => item?.Id))
            .OrderBy(item => item.Id)
            .ToArray();

        var packageSourceProvider = new PackageSourceProvider(new Settings(_solutionDirectory));
        var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, Repository.Provider.GetCoreV3());
        var repositories = sourceRepositoryProvider.GetRepositories().ToArray();

        using var cacheContext = new SourceCacheContext();

        var packages = (await Task.WhenAll(packageIdentities.Select(item => LoadPackage(item, repositories, cacheContext))));

        return packages.ExceptNullItems().ToArray();
    }

    private static async Task<PackageArchiveReader?> LoadPackage(PackageIdentity packageIdentity, IEnumerable<SourceRepository> repositories, SourceCacheContext cacheContext)
    {
        Output.WriteLine($"Load: {packageIdentity}");

        foreach (var repository in repositories)
        {
            var resource = await repository.GetResourceAsync<FindPackageByIdResource>();

            var packageStream = new MemoryStream();
            await resource.CopyNupkgToStreamAsync(packageIdentity.Id, packageIdentity.Version, packageStream, cacheContext,
                NullLogger.Instance, CancellationToken.None);

            packageStream.Position = 0;
            return new PackageArchiveReader(packageStream);
        }

        return null;
    }

    private async Task CreateLicenseFile(IEnumerable<PackageArchiveReader> packages)
    {
        var content = new StringBuilder("This product bundles the following components under the described licenses:\r\n\r\n");

        foreach (var package in packages)
        {
            var spec = new NuspecReader(package.GetNuspec());

            var packageId = spec.GetId();

            var projectUrl = spec.GetProjectUrl();

            if (string.IsNullOrEmpty(projectUrl))
            {
                Output.WriteLine($"Skip {packageId}: No project URL");
                continue;
            }

            if (_excludeRegex?.IsMatch(packageId) == true)
            {
                Output.WriteLine($"Skip {packageId}: Excluded");
                continue;
            }

            content.AppendLine(Delimiter)
                .AppendLine()
                .AppendLine(spec.GetTitle().NullWhenEmpty() ?? packageId)
                .AppendLine()
                .AppendLine($"Id:      {packageId}")
                .AppendLine($"Version: {spec.GetVersion()}")
                .AppendLine($"Project: {projectUrl}");

            var licenseMetadata = spec.GetLicenseMetadata();

            try
            {
                if (licenseMetadata != null)
                {
                    if (licenseMetadata.Type == LicenseType.Expression)
                    {
                        content.AppendLine($"License: {licenseMetadata.LicenseExpression}");
                    }
                    else
                    {
                        var license = licenseMetadata.License;
                        var file = package.GetEntry(license);
                        using var stream = new StreamReader(file.Open());
                        var lines = stream.ReadLines().ToArray();
                        if (lines.FirstOrDefault()?.Contains("MIT License") == true)
                        {
                            content.AppendLine("License: MIT");
                        }
                        else if (lines.Any(ExtensionMethods.IsApache2License))
                        {
                            content.AppendLine(ApacheLicenseExpression);
                        }
                        else
                        {
                            content.AppendLine("License:");
                            content.AppendLine(lines.FormatLicenseText());
                        }
                    }
                }
                else
                {
                    var licenseUrl = spec.GetLicenseUrl();
                    if (licenseUrl.IsApache2License())
                    {
                        content.AppendLine(ApacheLicenseExpression);
                    }
                    else
                    {
                        var lines = await DownloadLicense(licenseUrl);

                        if (lines.Any(ExtensionMethods.IsApache2License))
                        {
                            content.AppendLine(ApacheLicenseExpression);
                        }
                        else
                        {
                            content.AppendLine($"License: {licenseUrl}");

                            if (!lines.Any(line => line.StartsWith("<html", StringComparison.OrdinalIgnoreCase)))
                            {
                                content.AppendLine(lines.FormatLicenseText());
                            }
                        }
                    }
                }
            }
            catch
            {
                Output.WriteError($"Error loading license metadata for package {packageId}");
                throw;
            }

            content.AppendLine();
        }

        await File.WriteAllTextAsync(_outputPath, content.ToString());
    }

    private void AddProject(ProjectInfo projectInfo, int level = 0)
    {
        if (_includedProjects.ContainsKey(projectInfo.ProjectReference.RelativePath))
            return;

        _includedProjects.Add(projectInfo.ProjectReference.RelativePath, projectInfo);

        if (level > 0)
        {
            Output.WriteLine($"{new string(' ', 2 * level)}- {projectInfo.ProjectReference.RelativePath}");
        }

        ProjectInfo? FindProject(string referencePath)
        {
            var fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectInfo.ProjectReference.AbsolutePath) ?? ".", referencePath));

            return _projects.FirstOrDefault(project => string.Equals(project.ProjectReference.AbsolutePath, fullPath, StringComparison.OrdinalIgnoreCase));
        }

        var projectReferences = projectInfo.Project
            .GetItems("ProjectReference")
            .Select(item => item.EvaluatedInclude)
            .Select(FindProject)
            .ExceptNullItems()
            .ToArray();

        foreach (var reference in projectReferences)
        {
            AddProject(reference, level + 1);
        }
    }

    private bool ShouldInclude(ProjectInfo projectInfo)
    {
        if (_includedProjects.ContainsKey(projectInfo.ProjectReference.RelativePath))
            return false;

        var project = projectInfo.Project;

        var property = project.GetProperty(IsDeploymentTarget);

        return bool.TryParse(property?.EvaluatedValue, out var include) && include;
    }

    private static IEnumerable<ProjectInfo?> LoadProjects(SolutionFile solution)
    {
        foreach (var projectReference in solution.ProjectsInOrder)
        {
            if (projectReference.ProjectType == SolutionProjectType.SolutionFolder)
                continue;

            var projectInfo = default(ProjectInfo);

            try
            {
                Output.WriteLine($"Load: {projectReference.RelativePath}");
                projectInfo = new ProjectInfo(projectReference, new Project(projectReference.AbsolutePath));
            }
            catch (Exception ex)
            {
                Output.WriteWarning($"Loading failed: {ex.Message}");
            }

            yield return projectInfo;
        }
    }

    private sealed record ProjectInfo(ProjectInSolution ProjectReference, Project Project);

    private static async Task<ICollection<string>> DownloadLicense(string url)
    {
        try
        {
            using var stream = new StreamReader(await new HttpClient().GetStreamAsync(url));
            return stream.ReadLines().ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
