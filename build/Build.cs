using Nuke.Common;
using Nuke.Common.CI;
//using Nuke.Common.CI.TeamCity;
using Nuke.Common.Execution;
//using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Coverlet;
using Nuke.Common.Tools.DotCover;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.InspectCode;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Tools.Octopus;
using Nuke.Common.Tools.Paket;
using Nuke.Common.Tools.ReportGenerator;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using System;
using System.IO;
using System.Linq;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.InspectCode.InspectCodeTasks;
//using static Nuke.Common.Tools.NuGet.NuGetTasks;
using static Nuke.Common.Tools.Octopus.OctopusTasks;
using static Nuke.Common.Tools.Paket.PaketTasks;
using static Nuke.Common.Tools.ReportGenerator.ReportGeneratorTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
	/// Support plugins are available for:
	///   - JetBrains ReSharper        https://nuke.build/resharper
	///   - JetBrains Rider            https://nuke.build/rider
	///   - Microsoft VisualStudio     https://nuke.build/visualstudio
	///   - Microsoft VSCode           https://nuke.build/vscode

	public static int Main() => Execute<Build>(x => x.Octo_Pack);

	[Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
	readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

	//[CI] readonly TeamCity TeamCity;
	//[GitRepository] readonly GitRepository GitRepository;

	[Solution] readonly Solution Solution;
	[GitVersion] readonly GitVersion GitVersion;

	//TODO: try to replace the inline locators
	//[LocalExecutable("./.paket/paket.exe")]
	//readonly Tool Paket;

	AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
	AbsolutePath SourceDirectory => RootDirectory / "src";
	AbsolutePath NuGetOutputDirectory => ArtifactsDirectory / "NuGet";
	AbsolutePath OctopusOutputDirectory => ArtifactsDirectory / "Octo";

	const string NUGET_SERVER_KEY = "oy2cyqeqkfqnzll5f36qj37ojxdvtfyuzaaczn5sfmbpbm";
	const string NUGET_SERVER_URL = "https://www.nuget.org/api/v2/package";
	const string OCTOPACK_PUBLISH_APIKEY = "API-IUYK06GRIEZ0CPECQQ7V0FURMY";
	const string OCTOPUS_DEPLOY_SERVER = "http://icerep01:8081"; //TODO: convert to SSL
	readonly string OCTOPUS_PROJECT_NAME = Environment.GetEnvironmentVariable("OCTOPUS_PROJECT_NAME");// ?? "Nuke.Core";

	/// <summary>
	/// Runs JetBrains.ReSharper code analysis
	/// </summary>
	Target Analysis => _ => _
	.DependsOn(Restore)
	.Executes(() =>
	{
		InspectCode(_ => _
			.SetTargetPath(Solution)
			.SetOutput($"{ArtifactsDirectory}/inspectCode.xml")
			//.AddExtensions(
			//    "EtherealCode.ReSpeller",
			//    "PowerToys.CyclomaticComplexity",
			//    "ReSharper.ImplicitNullability",
			//    "ReSharper.SerializationInspections",
			//    "ReSharper.XmlDocInspections")
			);
	});

	/// <summary>
	/// Removes previously built artifacts
	/// </summary>
	Target Clean => _ => _
	.Before(Restore)
	.Executes(() =>
	{
		SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
		EnsureCleanDirectory(ArtifactsDirectory);
		DotNetClean();
	});

	/// <summary>
	/// This will restore all paket references
	/// </summary>
	Target Restore => _ => _.Executes(() => DotNetRestore(_ => _.SetProjectFile(Solution)));

	/// <summary>
	/// Build
	/// </summary>
	Target Compile => _ => _
	.DependsOn(Restore)
	.Executes(() =>
	{
		DotNetBuild(_ => _
			.SetProjectFile(Solution)
			.SetConfiguration(Configuration)
			.SetAssemblyVersion(GitVersion.AssemblySemVer)
			.SetFileVersion(GitVersion.AssemblySemFileVer)
			.SetInformationalVersion(GitVersion.InformationalVersion)
			.EnableNoRestore() // Doesn't perform an implicit restore during build
		);
	});

	// http://www.nuke.build/docs/authoring-builds/ci-integration.html#partitioning
	[Partition(2)] readonly Partition TestPartition;
	/// <summary>
	/// Runs all tests and generates report file(s)
	/// </summary>
	Target Test => _ => _
	.DependsOn(Compile)
	.Produces($"{ArtifactsDirectory}/*.trx")
	.Produces($"{ArtifactsDirectory}/*.xml")
	.Partition(() => TestPartition)
	.Executes(() =>
	{
		DotNetTest(_ => _
			.SetConfiguration(Configuration)
			.SetNoBuild(InvokedTargets.Contains(Compile))
			.ResetVerbosity()
			.SetResultsDirectory(ArtifactsDirectory)
			.When(InvokedTargets.Contains(Coverage) || IsServerBuild,
				_ => _
				.SetProperty("CollectCoverage", propertyValue: true)
				// CoverletOutputFormat: json (default), lcov, opencover, cobertura, teamcity
				.SetProperty("CoverletOutputFormat", "teamcity%2copencover")
				//.EnableCollectCoverage()
				//.SetCoverletOutputFormat(CoverletOutputFormat.teamcity)
				.When(IsServerBuild, _ => _
					.SetProperty("UseSourceLink", propertyValue: true)
					//.EnableUseSourceLink()
					)
				)
			.CombineWith(TestPartition.GetCurrent(Solution.GetProjects("*.UnitTest")), (_, v) => _
				.SetProjectFile(v)
				.SetLogger($"trx;LogFileName={v.Name}.trx")
				.When(InvokedTargets.Contains(Coverage) || IsServerBuild,
					_ => _
					.SetProperty("CoverletOutput", $"{ArtifactsDirectory}/{v.Name}.xml")
					//.SetCoverletOutput(ArtifactsDirectory / $"{v.Name}.xml")
					)
				)
			);

		//ArtifactsDirectory.GlobFiles("*.trx").ForEach(x =>
		//    Console.WriteLine(x.ToString())
		//    //AzurePipelines?.PublishTestResults(
		//    //    type: AzurePipelinesTestResultsType.VSTest,
		//    //    title: $"{Path.GetFileNameWithoutExtension(x)} ({AzurePipelines.StageDisplayName})",
		//    //    files: new string[] { x })
		//    );
	});

	string CoverageReportDirectory => $"{ArtifactsDirectory}/coverage-report";
	string CoverageReportArchive => $"{ArtifactsDirectory}/coverage-report.zip";
	/// <summary>
	/// Generates code coverage reports based on <see cref="Test">Test</see> results
	/// </summary>
	Target Coverage => _ => _
	.DependsOn(Test)
	//.TriggeredBy(Test)
	.Consumes(Test)
	.Produces(CoverageReportArchive)
	.Executes(() =>
	{
		//TODO: Handle error when XML files are not present
		ReportGenerator(_ => _
			//.SetFramework("netcoreapp2.1")
			.SetReports($"{ArtifactsDirectory}/*.xml")
			.SetReportTypes(ReportTypes.Html, ReportTypes.TeamCitySummary, ReportTypes.TextSummary)
			.SetTargetDirectory(CoverageReportDirectory)
		);

		//ArtifactsDirectory.GlobFiles("*.xml").ForEach(x =>
		//    Console.WriteLine(x.ToString())
		//    //AzurePipelines?.PublishCodeCoverage(
		//    //    AzurePipelinesCodeCoverageToolType.Cobertura,
		//    //    x,
		//    //    CoverageReportDirectory)
		//    );

		CompressZip(
			directory: CoverageReportDirectory,
			archiveFile: CoverageReportArchive,
			fileMode: FileMode.Create);
	});

	/// <summary>
	/// Creates NuGet package(s) that can be pushed to NuGet server.
	/// Implements Paket
	/// </summary>
	Target NuGet_Pack => _ => _
	.DependsOn(Test)
	.Produces($"{NuGetOutputDirectory}/*.nupkg")
	.Executes(() =>
	{
		PaketPack(_ => _
			.SetToolPath($"{RootDirectory}/.paket/paket.exe")
			.SetLockDependencies(true)
			.SetBuildConfiguration(Configuration)
			.SetPackageVersion(GitVersion.NuGetVersionV2)
			.SetOutputDirectory(NuGetOutputDirectory)
		);

		//DotNetPack(_ => _
		//	.SetProject(Solution)
		//	.SetNoBuild(ExecutingTargets.Contains(Compile))
		//	.SetConfiguration(Configuration)
		//	.SetOutputDirectory(NuGetOutputDirectory)
		//	.SetVersion(GitVersion.NuGetVersionV2)
		//	//.SetPackageReleaseNotes(GetNuGetReleaseNotes($"{RootDirectory}/CHANGELOG.md", GitRepository))
		//);
	});

	/// <summary>
	/// Pushes all packages generated from <see cref="NuGet_Pack">NuGet_Pack</see> to the NuGet repository
	/// Implements DotNetNuGet
	/// </summary>
	Target NuGet_Push => _ => _
	.DependsOn(NuGet_Pack)
	.Consumes(NuGet_Pack)
	.Executes(() =>
	{
		var packages = NuGetOutputDirectory.GlobFiles("*.nupkg");
		if (packages.Any())
		{
			DotNetNuGetPush(_ => _
				.SetApiKey(NUGET_SERVER_KEY)
				.SetSource(NUGET_SERVER_URL)
				.CombineWith(packages, (_, v) => _.SetTargetPath(v)),
				degreeOfParallelism: 5,
				completeOnFailure: true
			);

			//NuGetPush(_ => _
			//	.SetApiKey(NUGET_SERVER_KEY)
			//	.SetSource(NUGET_SERVER_URL)
			//	.CombineWith(packages, (_, v) => _.SetTargetPath(v)),
			//	degreeOfParallelism: 5,
			//	completeOnFailure: true
			//);

			//PaketPush(_ => _
			//	.SetToolPath($"{RootDirectory}/.paket/paket.exe")
			//	.SetApiKey(NUGET_SERVER_KEY)
			//	.SetUrl(NUGET_SERVER_URL)
			//	.CombineWith(packages, (_, v) => _.SetFile(v)),
			//	degreeOfParallelism: 5,
			//	completeOnFailure: true
			//);
		}
	});

	/// <summary>
	/// Uses <PropertyGroup><IsPublishable>...</IsPublishable></PropertyGroup> from the .csproj file
	/// Default == true
	/// Explicitly set to false for projects that do not need to be published
	/// </summary>
	Target Publish => _ => _
	.DependsOn(Test)
	.Executes(() =>
	{
		// Note: Solution.Projects only returns projects with no SolutionFolder property set
		Solution.AllProjects
			.Where(p => p.GetProperty("IsPublishable").EqualsOrdinalIgnoreCase("true"))
			.ForEach(p =>
			{
				DotNetPublish(_ => _
					.SetWorkingDirectory(p.Directory)
					.SetNoRestore(ExecutingTargets.Contains(Restore))
					.SetConfiguration(Configuration)
					.SetAssemblyVersion(GitVersion.AssemblySemVer)
					.SetFileVersion(GitVersion.AssemblySemFileVer)
					.SetInformationalVersion(GitVersion.InformationalVersion)
					.SetOutput($"{ArtifactsDirectory}/published-app/{p.Name}")
				);
			});
	});

	/// <summary>
	/// Generates NuGet packages for Octopus Deploy
	/// </summary>
	Target Octo_Pack => _ => _
	.DependsOn(Publish)
	.Produces($"{OctopusOutputDirectory}/*.nupkg")
	.Executes(() =>
	{
		// Note: Solution.Projects only returns projects with no SolutionFolder property set
		Solution.AllProjects
		.Where(p => p.GetProperty("IsPublishable").EqualsOrdinalIgnoreCase("true"))
		.ForEach(p =>
		{
			OctopusPack(_ => _
			.SetBasePath(p.Directory)
			.SetOutputFolder(OctopusOutputDirectory)
			.SetTitle(p.Name)
			.SetId(p.Name)
			.SetVersion(GitVersion.NuGetVersionV2)
			.SetOverwrite(true) // keeps from failing the build
		);
		});

		//TODO: Docker packages may need a different process
	});

	/// <summary>
	/// Pushes all packages generated from <see cref="Octo_Pack">Octo_Pack</see> to the Octopus repository
	/// </summary>
	Target Octo_Push => _ => _
	.DependsOn(Octo_Pack)
	.Consumes(Octo_Pack)
	.Executes(() =>
	{
		var packages = OctopusOutputDirectory.GlobFiles("*.nupkg");
		if (packages.Any())
		{
			OctopusPush(_ => _
			.SetServer(OCTOPUS_DEPLOY_SERVER)
			.SetApiKey(OCTOPACK_PUBLISH_APIKEY)
			.EnableReplaceExisting() // keeps from failing the build
			.CombineWith(packages, (_, v) => _.SetPackage(v))
			);
		}
	});


	Target Octo_Create_Release => _ => _
	.DependsOn(Octo_Push)
	.Requires(() => !string.IsNullOrWhiteSpace(OCTOPUS_PROJECT_NAME))
	.Executes(() =>
	{
		OctopusCreateRelease(_ => _
		.SetServer(OCTOPUS_DEPLOY_SERVER)
		.SetApiKey(OCTOPACK_PUBLISH_APIKEY)
		.SetProject(OCTOPUS_PROJECT_NAME)
		.SetEnableServiceMessages(true)
		.SetDefaultPackageVersion(GitVersion.NuGetVersionV2)
		.SetVersion(GitVersion.NuGetVersionV2)
		.SetReleaseNotes("")
		);
	});
}
