using System.Text;
using System.Text.RegularExpressions;

using LicenseGenerator;

using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.ProjectModel;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

using TomsToolbox.Essentials;

using static Constants;

internal sealed class Builder : IDisposable
{
    private static readonly string Delimiter = new('-', 80);

    private readonly ProjectInfo[] _projects;
    private readonly Dictionary<string, ProjectInfo> _includedProjects = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _outputPath;
    private readonly bool _recursive;
    private readonly string _solutionDirectory;
    private readonly Regex? _excludeRegex;
    private readonly Dictionary<NuGetFramework, TargetFrameworkCollection> _targetFrameworkCollections = new();

    public Builder(FileInfo input, string output, string? exclude, bool recursive)
    {
        _outputPath = output;
        _recursive = recursive;
        _solutionDirectory = input.DirectoryName ?? ".";
        _excludeRegex = string.IsNullOrEmpty(exclude) ? null : new Regex(exclude, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        if (string.IsNullOrEmpty(Path.GetDirectoryName(_outputPath)))
        {
            _outputPath = Path.Combine(_solutionDirectory, _outputPath);
        }

        Output.WriteLine($"Solution: '{input}'");
        Output.WriteLine();

        var solution = SolutionFile.Parse(input.FullName);

        _projects = LoadProjects(solution, _targetFrameworkCollections)
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

        var content = await CreateLicenseFile(packages);

        await File.WriteAllTextAsync(_outputPath, content);

        return 0;
    }

    private async Task<ICollection<PackageArchiveReader>> LoadPackages()
    {
        var settings = Settings.LoadDefaultSettings(_solutionDirectory);
        var globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
        var packageSourceProvider = new PackageSourceProvider(settings);
        var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, Repository.Provider.GetCoreV3());
        var repositories = sourceRepositoryProvider.GetRepositories().ToArray();

        using var cacheContext = new SourceCacheContext();
        var downloadContext = new PackageDownloadContext(cacheContext);

        var resolvedPackages = new Dictionary<string, PackageArchiveReader>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in _includedProjects.Values)
        {
            var frameworkSpecificProjects = project.GetFrameworkSpecificProjects().ToArray();

            foreach (var frameworkSpecificProject in frameworkSpecificProjects)
            {
                foreach (var packageIdentity in GetPackageIdentities(frameworkSpecificProject))
                {
                    await LoadPackage(frameworkSpecificProject, packageIdentity, repositories, downloadContext, resolvedPackages, globalPackagesFolder);
                }
            }
        }

        return resolvedPackages.Values;
    }

    private static IEnumerable<PackageIdentity> GetPackageIdentities(FrameworkSpecificProject project)
    {
        foreach (var packageReference in project.Project.AllEvaluatedItems.Where(item => item.ItemType == "PackageReference"))
        {
            if (packageReference.GetMetadata("PrivateAssets") != null)
                continue;

            if (packageReference.GetMetadata("ExcludeAssets")?.EvaluatedValue.Contains("runtime") == true)
                continue;

            var identity = packageReference.EvaluatedInclude;
            var version = packageReference.GetMetadata("Version")?.EvaluatedValue;

            if (string.IsNullOrEmpty(version))
                continue;

            yield return GetPackageIdentity(project, identity, version);
        }
    }

    private static PackageIdentity GetPackageIdentity(FrameworkSpecificProject project, string identity, string? version = null)
    {
        var nugetVersion = GetPackageVersion(project, identity, version);

        var packageIdentity = new PackageIdentity(identity, nugetVersion);

        return packageIdentity;
    }

    private static NuGetVersion GetPackageVersion(FrameworkSpecificProject project, string identity, string? version = null)
    {
        if (NuGetVersion.TryParse(version, out var nugetVersion))
            return nugetVersion;

        // version is not a simple version string, but maybe a version range like "[1.0-2.0)" or "1.0.*"
        // => try to read the restored version from the lock file
        try
        {
            var lockFile = project.LockFile;
            nugetVersion = lockFile.Libraries.Single(library => library.Name == identity).Version;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to find unique version of package {identity}, restoring nuget packages first may fix this.", ex);
        }

        return nugetVersion;
    }

    private static NuGetFramework ToPlatformVersionIndependent(NuGetFramework framework)
    {
        return new NuGetFramework(framework.Framework, framework.Version, framework.Platform, new Version());
    }

    private static NuGetFramework? GetNearestFramework(ICollection<NuGetFramework> items, NuGetFramework framework)
    {
        return NuGetFrameworkUtility.GetNearest(items, framework, item => item)
               // also match "net6.0-windows7.0" and "net6.0-windows"
               ?? NuGetFrameworkUtility.GetNearest(items, ToPlatformVersionIndependent(framework), ToPlatformVersionIndependent);
    }

    private static T? GetNearestFramework<T>(ICollection<T> items, NuGetFramework framework)
        where T : class, IFrameworkSpecific
    {
        return NuGetFrameworkUtility.GetNearest(items, framework)
               // also match "net6.0-windows7.0" and "net6.0-windows"
               ?? NuGetFrameworkUtility.GetNearest(items, ToPlatformVersionIndependent(framework), item => ToPlatformVersionIndependent(item.TargetFramework));
    }

    private async Task LoadPackage(FrameworkSpecificProject project, PackageIdentity packageIdentity, ICollection<SourceRepository> repositories, PackageDownloadContext context, IDictionary<string, PackageArchiveReader> resolvedPackages, string globalPackagesFolder)
    {
        List<string> lastExceptions = new List<string>();

        if (resolvedPackages.TryGetValue(packageIdentity.Id, out var existingPackage) && existingPackage.GetIdentity().Version >= packageIdentity.Version)
        {
            return;
        }

        Output.WriteLine($"Load: {packageIdentity}");

        foreach (var repository in repositories)
        {
            DownloadResourceResult downloadResult;

            try
            {
                var downloadResource = await repository.GetResourceAsync<DownloadResource>();

                downloadResult = await downloadResource.GetDownloadResourceResultAsync(packageIdentity, context, globalPackagesFolder, NullLogger.Instance, CancellationToken.None);
            }
            catch (NuGetProtocolException ex)
            {
                lastExceptions.Add(ex.Message);
                continue;  // Try next repo
            }

            if (downloadResult.Status != DownloadResourceResultStatus.Available)
                continue;  // Try next repo

            var packageStream = downloadResult.PackageStream;
            packageStream.Position = 0;

            var package = new PackageArchiveReader(packageStream);

            resolvedPackages[packageIdentity.Id] = package;

            await using var nuspec = package.GetNuspec();

            bool ScanDependencies()
            {
                // Don't scan packages with pseudo-references, they don't get physically included.
                if (string.Equals(packageIdentity.Id, "NETStandard.Library", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (_recursive)
                    return true;

                if (!string.IsNullOrEmpty(new NuspecReader(nuspec).GetProjectUrl()))
                    return false;

                Output.WriteLine($"  - No project url found in {packageIdentity}, scanning dependencies");
                return true;

            }

            if (!ScanDependencies())
                return;

            var packageDependencies = package.GetPackageDependencies()?.ToArray();
            if (packageDependencies is null)
                return;

            var bestMatching = GetNearestFramework(packageDependencies, project.TargetFramework);
            var dependencies = bestMatching?.Packages
                               ?? packageDependencies.SelectMany(item => item.Packages).Distinct();

            foreach (var dependency in dependencies)
            {
                try
                {
                    var identity = GetPackageIdentity(project, dependency.Id);
                    await LoadPackage(project, identity, repositories, context, resolvedPackages, globalPackagesFolder);
                }
                catch (InvalidOperationException)
                {
                    // Ignore dependencies that can't be loaded.
                }
            }
            return;
        }

        throw new InvalidOperationException($"Package {packageIdentity} not found in any of the configured repositories: {string.Join(", ", lastExceptions)}");
    }

    private async Task<string> CreateLicenseFile(IEnumerable<PackageArchiveReader> packages)
    {
        var content = new StringBuilder("This product bundles the following components under the described licenses:\r\n\r\n");

        foreach (var package in packages.OrderBy(p => p.GetIdentity().Id))
        {
            await using var nuspec = package.GetNuspec();

            var spec = new NuspecReader(nuspec);

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

        return content.ToString();
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

    private static IEnumerable<ProjectInfo?> LoadProjects(SolutionFile solution, Dictionary<NuGetFramework, TargetFrameworkCollection> targetFrameworkCollections)
    {
        foreach (var projectReference in solution.ProjectsInOrder)
        {
            if (projectReference.ProjectType == SolutionProjectType.SolutionFolder)
                continue;

            var projectInfo = default(ProjectInfo);

            try
            {
                Output.WriteLine($"Load: {projectReference.RelativePath}");

                var project = new Project(projectReference.AbsolutePath);

                projectInfo = new ProjectInfo(projectReference, project, GetTargetFrameworks(project), targetFrameworkCollections);
            }
            catch (Exception ex)
            {
                Output.WriteWarning($"Loading failed: {ex.Message}");
            }

            yield return projectInfo;
        }
    }

    private static NuGetFramework[] GetTargetFrameworks(Project project)
    {
        var frameworkNames = (project.GetProperty("TargetFrameworks") ?? project.GetProperty("TargetFramework"))
            ?.EvaluatedValue
            .Split(';')
            .Select(value => value.Trim());

        var frameworks = frameworkNames?
            .Select(NuGetFramework.Parse)
            .Distinct()
            .ToArray();

        return frameworks ?? new[] { NuGetFramework.AnyFramework };
    }

    private sealed record ProjectInfo(ProjectInSolution ProjectReference, Project Project, NuGetFramework[] TargetFrameworks, Dictionary<NuGetFramework, TargetFrameworkCollection> TargetFrameworkCollections)
    {
        public IEnumerable<FrameworkSpecificProject> GetFrameworkSpecificProjects()
        {
            return TargetFrameworks.Select(GetProjectInFramework);
        }

        private FrameworkSpecificProject GetProjectInFramework(NuGetFramework targetFramework)
        {
            if (TargetFrameworks.Length <= 1)
                return new FrameworkSpecificProject(Project, TargetFrameworks.FirstOrDefault() ?? NuGetFramework.AnyFramework);

            var bestMatching = GetNearestFramework(TargetFrameworks, targetFramework);
            if (bestMatching == null)
                return new FrameworkSpecificProject(Project, TargetFrameworks.FirstOrDefault() ?? NuGetFramework.AnyFramework);

            var collection = TargetFrameworkCollections.ForceValue(bestMatching, framework => new TargetFrameworkCollection(framework));

            return new FrameworkSpecificProject(collection.LoadProject(ProjectReference.AbsolutePath), bestMatching);
        }
    }

    private sealed record FrameworkSpecificProject(Project Project, NuGetFramework TargetFramework)
    {
        private LockFile? _lockFile;

        public LockFile LockFile => _lockFile ??= LockFileUtilities.GetLockFile(Project.GetPropertyValue("ProjectAssetsFile"), NullLogger.Instance);
    }

    private sealed class TargetFrameworkCollection : IDisposable
    {
        private readonly ProjectCollection _projectCollection;

        public TargetFrameworkCollection(NuGetFramework framework)
        {
            var properties = new Dictionary<string, string>
            {
                { "TargetFramework", framework.GetShortFolderName() }
            };

            _projectCollection = new ProjectCollection(properties);
        }

        public Project LoadProject(string filePath)
        {
            return _projectCollection.LoadProject(filePath);
        }

        public void Dispose()
        {
            _projectCollection.Dispose();
        }
    }

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

    public void Dispose()
    {
        foreach (var collection in _targetFrameworkCollections.Values)
        {
            collection.Dispose();
        }
    }
}
