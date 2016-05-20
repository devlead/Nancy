// Usings
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

// Arguments
var target = Argument<string>("target", "Default");
var source = Argument<string>("source", null);
var apiKey = Argument<string>("apikey", null);
var version = Argument<string>("targetversion", null);
var skipClean = Argument<bool>("skipclean", false);
var skipTests = Argument<bool>("skiptests", false);

// Variables
var configuration = IsRunningOnWindows() ? "Release" : "MonoRelease";
var solution = File("./Nancy.sln");
var sharedAssemblyInfo = File("./SharedAssemblyInfo.cs");
var nancyVersion = "0.0.0.0";

// Directories
var output = Directory("build");
var outputBinaries = output + Directory("binaries");
var outputBinariesNet452 = outputBinaries + Directory("net452");
var outputBinariesNetstandard = outputBinaries + Directory("netstandard1.5");
var outputPackages = output + Directory("packages");
var outputNuGet = output + Directory("nuget");

///////////////////////////////////////////////////////////////

Setup(context =>
{
  // Parse the Nancy version.
  var assemblyInfo = ParseAssemblyInfo(sharedAssemblyInfo);
  nancyVersion = assemblyInfo.AssemblyVersion;
  Information("Version: {0}", nancyVersion);
});

///////////////////////////////////////////////////////////////

Task("Clean")
  .Does(() =>
{
  // Clean artifact directories.
  CleanDirectories(new DirectoryPath[] {
    output, outputBinaries, outputPackages, outputNuGet,
    outputBinariesNet452, outputBinariesNetstandard
  });

  if(!skipClean) {
    // Clean output directories.
    CleanDirectories("./src/**/" + configuration);
    CleanDirectories("./test/**/" + configuration);
    CleanDirectories("./samples/**/" + configuration);
  }
});

Task("Restore-NuGet-Packages")
  .Description("Restores NuGet packages")
  .Does(() =>
{
  var settings = new DotNetCoreRestoreSettings
  {
    Verbose = false,
    Verbosity = DotNetCoreRestoreVerbosity.Warning
  };

  DotNetCoreRestore("./src", settings);
  DotNetCoreRestore("./samples", settings);
  DotNetCoreRestore("./test", settings);
});

Task("Compile")
  .Description("Builds the solution")
  .IsDependentOn("Clean")
  .IsDependentOn("Restore-NuGet-Packages")
  .Does(() =>
{
  var projects = GetFiles("./**/*.xproj");
  foreach(var project in projects)
  {
    DotNetCoreBuild(project.GetDirectory().FullPath, new DotNetCoreBuildSettings {
      Configuration = configuration,
      Verbose = false
    });
  }
});

Task("Test")
  .Description("Executes xUnit tests")
  .WithCriteria(!skipTests)
  .IsDependentOn("Compile")
  .Does(() =>
{
  var projects = GetFiles("./test/**/*.xproj")
    - GetFiles("./test/**/Nancy.ViewEngines.Razor.Tests.Models.xproj");

  foreach(var project in projects)
  {
    DotNetCoreTest(project.GetDirectory().FullPath, new DotNetCoreTestSettings {
      Configuration = configuration
    });
  }
});

Task("Publish")
  .Description("Gathers output files and copies them to the output folder")
  .IsDependentOn("Compile")
  .Does(() =>
{
  // Copy net452 binaries.
  CopyFiles(GetFiles("src/**/bin/" + configuration + "/net452/*.dll")
    + GetFiles("src/**/bin/" + configuration + "/net452/*.xml")
    + GetFiles("src/**/bin/" + configuration + "/net452/*.pdb")
    + GetFiles("src/**/*.ps1"), outputBinariesNet452);

  // Copy netstandard1.5 binaries.
  CopyFiles(GetFiles("src/**/bin/" + configuration + "/netstandard1.5/*.dll")
    + GetFiles("src/**/bin/" + configuration + "/netstandard1.5/*.xml")
    + GetFiles("src/**/bin/" + configuration + "/netstandard1.5/*.pdb")
    + GetFiles("src/**/*.ps1"), outputBinariesNetstandard);

});

Task("Package")
  .Description("Zips up the built binaries for easy distribution")
  .IsDependentOn("Publish")
  .Does(() =>
{
  var package = outputPackages + File("Nancy-Latest.zip");
  var files = GetFiles(outputBinaries.Path.FullPath + "/**/*");

  Zip(outputBinaries, package, files);
});

Task("Nuke-Symbol-Packages")
  .Description("Deletes symbol packages")
  .Does(() =>
{
  DeleteFiles(GetFiles("./**/*.Symbols.nupkg"));
});

Task("Package-NuGet")
  .Description("Generates NuGet packages for each project that contains a nuspec")
  .IsDependentOn("Publish")
  .Does(() =>
{
  var projects = GetFiles("./**/*.xproj");
  foreach(var project in projects)
  {
    var settings = new DotNetCorePackSettings {
        Configuration = "Release",
        OutputDirectory = outputNuGet
    };

    DotNetCorePack(project.GetDirectory().FullPath, settings);
  }
});

Task("Publish-NuGet")
  .Description("Pushes the nuget packages in the nuget folder to a NuGet source. Also publishes the packages into the feeds.")
  .Does(() =>
{
  // Make sure we have an API key.
  if(string.IsNullOrWhiteSpace(apiKey)){
    throw new CakeException("No NuGet API key provided.");
  }

  // Upload every package to the provided NuGet source (defaults to nuget.org).
  var packages = GetFiles(outputNuGet.Path.FullPath + "/*" + version + ".nupkg");
  foreach(var package in packages)
  {
    NuGetPush(package, new NuGetPushSettings {
        Source = source,
        ApiKey = apiKey
    });
  }
});

///////////////////////////////////////////////////////////////

Task("Tag")
    .Description("Tags the current release.")
    .Does(() =>
{
  StartProcess("git", new ProcessSettings {
      Arguments = string.Format("tag \"v{0}\"", nancyVersion)
  });
});

Task("Prepare-Release")
    .Does(() =>
{
  // Update version.
  UpdateSharedAssemblyInfo(version, null);

  // Add
  StartProcess("git", new ProcessSettings {
      Arguments = string.Format("add {0}", sharedAssemblyInfo.Path.FullPath)
  });
  // Commit
  StartProcess("git", new ProcessSettings {
      Arguments = string.Format("commit -m \"Updated version to {0}\"", version)
  });
  // Tag
  StartProcess("git", new ProcessSettings {
      Arguments = string.Format("tag \"v{0}\"", version)
  });
});

///////////////////////////////////////////////////////////////

Task("Update-Informational-Version")
  .Does(() =>
{
  UpdateSharedAssemblyInfo(null, version);
});

Task("Update-Version")
  .Does(() =>
{
  if(string.IsNullOrWhiteSpace(version)) {
    throw new CakeException("No version specified!");
  }
  UpdateSharedAssemblyInfo(version, null);
});

///////////////////////////////////////////////////////////////

public void UpdateSharedAssemblyInfo(string assemblyVersion, string informationalVersion)
{
    // Make sure at least one version was specified.
    if(string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(informationalVersion)) {
        throw new CakeException("No version specified!");
    }

    // Parse the existing assembly info file.
    var info = ParseAssemblyInfo(sharedAssemblyInfo);

    // Create a new assembly info file.
    CreateAssemblyInfo(sharedAssemblyInfo, new AssemblyInfoSettings {
      Company = info.Company,
      Copyright = info.Copyright,
      Description = info.Description,
      InformationalVersion = informationalVersion ?? info.AssemblyInformationalVersion,
      Product = info.Product,
      Title = info.Title,
      Version = assemblyVersion ?? info.AssemblyVersion
    });
}

///////////////////////////////////////////////////////////////

Task("Default")
  .IsDependentOn("Test")
  .IsDependentOn("Package");

Task("Mono")
  .IsDependentOn("Test");

///////////////////////////////////////////////////////////////

RunTarget(target);
