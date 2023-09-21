# License Generator

## A DotNet tool to create a license file AKA NOTICE.TXT for all referenced nuget packages. 
[![Build](https://github.com/tom-englert/LicenseGenerator/actions/workflows/build.yml/badge.svg)](https://github.com/tom-englert/LicenseGenerator/actions/workflows/build.yml)
[![Nuget](https://img.shields.io/nuget/v/TomsToolbox.LicenseGenerator)](https://www.nuget.org/packages/TomsToolbox.LicenseGenerator)

This tool uses MSBuild logic to control which projects should be listed in the license file, giving you a high grade of flexibility.

All projects containing a property `IsDeploymentTarget` set to `true` and their dependencies are included in the license file.

You can mark individual projects by adding the `IsDeploymentTarget` property to the project file:
    
- Include the project and all references:
```xml
    <IsDeploymentTarget>true</IsDeploymentTarget>
```    

You can include several projects by convention by adding conditional properties in the Directory.Build.targets file:
    
- Include all executables:
```xml
    <IsDeploymentTarget Condition="'$(IsDeploymentTarget)'=='' AND '$(IsTestProject)'!='True' AND '$(OutputType)'=='Exe'">true</IsDeploymentTarget>
```
    
- Include all projects ending with `Something`:
```xml
    <_IsSomeProject>$(MSBuildProjectName.ToUpperInvariant().EndsWith("SOMETHING"))</_IsSomeProject>
    <IsDeploymentTarget Condition="'$(IsDeploymentTarget)'=='' AND $(_IsSomeProject)">true</IsDeploymentTarget>
```
    
## Installation
```
dotnet tool install TomsToolbox.LicenseGenerator -g
```
## Usage
```
build-license [options]
```
### Options
```
  -i, --input <input> (REQUIRED)  The path to the solution file to process.
  -o, --output <output>           The name of the license file that is created.
                                  An existing file will be overwritten without confirmation.
                                  Default is Notice.txt in the same folder as the solution.
  -e, --exclude <exclude>         A regular expression to specify package ids to exclude from output.
  --recursive                     A flag to indicate that all dependencies should be scanned recursively.
  --offline                       A flag to indicate that only the locally cached packages should be scanned (requires a restor beforehand).
  --version                       Show version information
  -?, -h, --help                  Show help and usage information
```






