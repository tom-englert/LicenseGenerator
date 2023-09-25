using System.CommandLine;

using Microsoft.Build.Locator;

using static Constants;

#pragma warning disable CA1852 // Seal internal types (Program is auto generated)
var returnValue = 0;
#pragma warning restore CA1852 // Seal internal types

const string description = $"""
    A DotNet tool to create a license file AKA NOTICE.TXT for all referenced nuget packages.

    All projects containing a property `{IsDeploymentTarget}` set to `true` and their dependencies are included in the license file.

    You can mark individual projects by adding the `{IsDeploymentTarget}` property to the project file:

    - Include the project and all references:
        <{IsDeploymentTarget}>true</{IsDeploymentTarget}>

    You can include several projects by convention by adding conditional properties in the Directory.Build.targets file:

    - Include all executables:
        <{IsDeploymentTarget} Condition="'$({IsDeploymentTarget})'=='' AND '$(IsTestProject)'!='True' AND '$(OutputType)'=='Exe'">true</{IsDeploymentTarget}>

    - Include all projects ending with `Something`:
        <_IsSomeProject>$(MSBuildProjectName.ToUpperInvariant().EndsWith("SOMETHING"))</_IsSomeProject>
        <{IsDeploymentTarget} Condition="'$({IsDeploymentTarget})'=='' AND $(_IsSomeProject)">true</{IsDeploymentTarget}>
    """;

var rootCommand = new RootCommand
{
    Name = "build-license",
    Description = description
};

var inputOption = new Option<FileInfo>(new[] { "--input", "-i" }, """
    The path to the solution file to process. 
    """) { IsRequired = true };

var outputOption = new Option<string?>(new[] { "--output", "-o" }, """
    The name of the license file that is created.
    An existing file will be overwritten without confirmation.
    Default is Notice.txt in the same folder as the solution.
    """) { IsRequired = false };

var excludeOption = new Option<string?>(new[] { "--exclude", "-e" }, """
    A regular expression to specify package ids to exclude from output.
    """) { IsRequired = false };

var recursiveOption = new Option<bool>(new[] { "--recursive" }, """
    A flag to indicate that all dependencies should be scanned recursively.
    """) { IsRequired = false };

var offlineOption = new Option<bool>(new[] { "--offline" }, """
    A flag to indicate that only the locally cached packages should be scanned (requires a restore beforehand).
    """) { IsRequired = false };

rootCommand.AddOption(inputOption);
rootCommand.AddOption(outputOption);
rootCommand.AddOption(excludeOption);
rootCommand.AddOption(recursiveOption);
rootCommand.AddOption(offlineOption);
rootCommand.SetHandler(async (input, output, exclude, recursive, offline) => returnValue = await Run(input, output, exclude, recursive, offline), inputOption, outputOption, excludeOption, recursiveOption, offlineOption);

await rootCommand.InvokeAsync(args);

return returnValue;

static async Task<int> Run(FileInfo input, string? output, string? exclude, bool recursive, bool offline)
{
    var visualStudioInstance = MSBuildLocator.QueryVisualStudioInstances().MaxBy(instance => instance.Version);
    MSBuildLocator.RegisterInstance(visualStudioInstance);

    try
    {
        using var builder = new Builder(input, output ?? "Notice.txt", exclude, recursive, offline);
        return await builder.Build();
    }
    catch (Exception ex)
    {
        Output.WriteError($"Execution failed: {ex.Message}");
        return 1;
    }
}
