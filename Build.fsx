#r @"packages/FAKE.Core/tools/FakeLib.dll"

open Fake
open Fake.Testing
open System
open System.Diagnostics;
open System.Text.RegularExpressions

let releaseFolder = "Release"
let nunitToolsFolder = "Packages/NUnit.Runners.2.6.2/tools"
let nuGetOutputFolder = "NuGetPackages"
let nuGetPackages = !! (nuGetOutputFolder @@ "*.nupkg" )
                    // Skip symbol packages because NuGet publish symbols automatically when package is published
                    -- (nuGetOutputFolder @@ "*.symbols.nupkg")
                    // Currently AutoFakeItEasy2 has been deprecated and is not being published to the feeds.
                    -- (nuGetOutputFolder @@ "AutoFixture.AutoFakeItEasy2.*" )
let solutionsToBuild = !! "Src/All.sln"
let processorArchitecture = environVar "PROCESSOR_ARCHITECTURE"

type BuildVersionInfo = { assemblyVersion:string; fileVersion:string; infoVersion:string; nugetVersion:string }
let calculateVersionFromGit buildNumber =
    // Example of output for a release tag: v3.50.2-288-g64fd5c5b, for a prerelease tag: v3.50.2-alpha1-288-g64fd5c5b
    let desc = Git.CommandHelper.runSimpleGitCommand "" "describe --tags --long --match=v*"

    let result = Regex.Match(desc,
                             @"^v(?<maj>\d+)\.(?<min>\d+)\.(?<rev>\d+)(?<pre>-\w+\d*)?-(?<num>\d+)-g(?<sha>[a-z0-9]+)$",
                             RegexOptions.IgnoreCase)
                      .Groups
    let getMatch (name:string) = result.[name].Value

    let major, minor, revision, preReleaseSuffix, commitsNum, sha =
        getMatch "maj" |> int, getMatch "min" |> int, getMatch "rev" |> int, getMatch "pre", getMatch "num" |> int, getMatch "sha"

    
    let assemblyVersion = sprintf "%d.%d.%d.0" major minor revision
    let fileVersion = sprintf "%d.%d.%d.%d" major minor revision buildNumber
    
    // If number of commits since last tag is greater than zero, we append another identifier with number of commits.
    // The produced version is larger than the last tag version.
    // If we are on a tag, we use version specified modification.
    // Examples of output: 3.50.2.1, 3.50.2.215, 3.50.1-rc1.3, 3.50.1-rc3.35
    let nugetVersion = match commitsNum with
                       | 0 -> sprintf "%d.%d.%d%s" major minor revision preReleaseSuffix
                       | _ -> sprintf "%d.%d.%d%s.%d" major minor revision preReleaseSuffix commitsNum

    let infoVersion = match commitsNum with
                      | 0 -> nugetVersion
                      | _ -> sprintf "%s-%s" nugetVersion sha

    { assemblyVersion=assemblyVersion; fileVersion=fileVersion; infoVersion=infoVersion; nugetVersion=nugetVersion }

// Calculate version that should be used for the build. Define globally as data might be required by multiple targets.
// Please never name the build parameter with version as "Version" - it might be consumed by the MSBuild, override 
// the defined properties and break some tasks (e.g. NuGet restore).
let buildVersion = match getBuildParamOrDefault "BuildVersion" "git" with
                   | "git"       -> calculateVersionFromGit (getBuildParamOrDefault "BuildNumber" "0" |> int)
                   | assemblyVer -> { assemblyVersion = assemblyVer
                                      fileVersion = getBuildParamOrDefault "BuildFileVersion" assemblyVer
                                      infoVersion = getBuildParamOrDefault "BuildInfoVersion" assemblyVer
                                      nugetVersion = getBuildParamOrDefault "BuildNugetVersion" assemblyVer }



Target "PatchAssemblyVersions" (fun _ ->
    printfn 
        "Patching assembly versions. Assembly version: %s, File version: %s, Informational version: %s" 
        buildVersion.assemblyVersion
        buildVersion.fileVersion
        buildVersion.infoVersion

    let filesToPatch = !! "Src/*/Properties/AssemblyInfo.*"
    ReplaceAssemblyInfoVersionsBulk filesToPatch 
                                    (fun f -> { f with AssemblyVersion              = buildVersion.assemblyVersion
                                                       AssemblyFileVersion          = buildVersion.fileVersion
                                                       AssemblyInformationalVersion = buildVersion.infoVersion })
)

let build target configuration =
    let keyFile =
        match getBuildParam "signkey" with
        | "" -> []
        | x  -> [ "AssemblyOriginatorKeyFile", FullName x ]

    let properties = keyFile @ [ "Configuration", configuration
                                 "AssemblyVersion", buildVersion.assemblyVersion
                                 "FileVersion", buildVersion.fileVersion
                                 "InformationalVersion", buildVersion.infoVersion ]

    solutionsToBuild
    |> MSBuild "" target properties
    |> ignore

let clean   = build "Clean"
let rebuild = build "Rebuild"

Target "CleanAll"           (fun _ -> ())
Target "CleanVerify"        (fun _ -> clean "Verify")
Target "CleanRelease"       (fun _ -> clean "Release")
Target "CleanReleaseFolder" (fun _ -> CleanDir releaseFolder)

Target "Verify" (fun _ -> rebuild "Verify")

Target "BuildOnly" (fun _ -> rebuild "Release")
Target "TestOnly" (fun _ ->
    let configuration = getBuildParamOrDefault "Configuration" "Release"
    let parallelizeTests = getBuildParamOrDefault "ParallelizeTests" "False" |> Convert.ToBoolean
    let maxParallelThreads = getBuildParamOrDefault "MaxParallelThreads" "0" |> Convert.ToInt32
    let parallelMode = if parallelizeTests then ParallelMode.All else ParallelMode.NoParallelization
    let maxThreads = if maxParallelThreads = 0 then CollectionConcurrencyMode.Default else CollectionConcurrencyMode.MaxThreads(maxParallelThreads)

    let testAssemblies = !! (sprintf "Src/*Test/bin/%s/*Test.dll" configuration)
                         -- (sprintf "Src/AutoFixture.NUnit*.*Test/bin/%s/*Test.dll" configuration)

    testAssemblies
    |> xUnit2 (fun p -> { p with Parallel = parallelMode
                                 MaxThreads = maxThreads })

    let nunit2TestAssemblies = !! (sprintf "Src/AutoFixture.NUnit2.*Test/bin/%s/*Test.dll" configuration)

    nunit2TestAssemblies
    |> NUnit (fun p -> { p with StopOnError = false
                                OutputFile = "NUnit2TestResult.xml" })

    let nunit3TestAssemblies = !! (sprintf "Src/AutoFixture.NUnit3.UnitTest/bin/%s/Ploeh.AutoFixture.NUnit3.UnitTest.dll" configuration)

    nunit3TestAssemblies
    |> NUnit3 (fun p -> { p with StopOnError = false
                                 ResultSpecs = ["NUnit3TestResult.xml;format=nunit2"] })
)

Target "BuildAndTestOnly" (fun _ -> ())
Target "Build" (fun _ -> ())
Target "Test"  (fun _ -> ())

Target "CopyToReleaseFolder" (fun _ ->
    let buildOutput = [
      "Src/AutoFixture/bin/Release/Ploeh.AutoFixture.dll";
      "Src/AutoFixture/bin/Release/Ploeh.AutoFixture.pdb";
      "Src/AutoFixture/bin/Release/Ploeh.AutoFixture.XML";
      "Src/SemanticComparison/bin/Release/Ploeh.SemanticComparison.dll";
      "Src/SemanticComparison/bin/Release/Ploeh.SemanticComparison.pdb";
      "Src/SemanticComparison/bin/Release/Ploeh.SemanticComparison.XML";
      "Src/AutoMoq/bin/Release/Ploeh.AutoFixture.AutoMoq.dll";
      "Src/AutoMoq/bin/Release/Ploeh.AutoFixture.AutoMoq.pdb";
      "Src/AutoMoq/bin/Release/Ploeh.AutoFixture.AutoMoq.XML";
      "Src/AutoRhinoMock/bin/Release/Ploeh.AutoFixture.AutoRhinoMock.dll";
      "Src/AutoRhinoMock/bin/Release/Ploeh.AutoFixture.AutoRhinoMock.pdb";
      "Src/AutoRhinoMock/bin/Release/Ploeh.AutoFixture.AutoRhinoMock.XML";
      "Src/AutoFakeItEasy/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy.dll";
      "Src/AutoFakeItEasy/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy.pdb";
      "Src/AutoFakeItEasy/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy.XML";
      "Src/AutoFakeItEasy2/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy2.dll";
      "Src/AutoFakeItEasy2/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy2.pdb";
      "Src/AutoFakeItEasy2/bin/Release/Ploeh.AutoFixture.AutoFakeItEasy2.XML";
      "Src/AutoNSubstitute/bin/Release/Ploeh.AutoFixture.AutoNSubstitute.dll";
      "Src/AutoNSubstitute/bin/Release/Ploeh.AutoFixture.AutoNSubstitute.pdb";
      "Src/AutoNSubstitute/bin/Release/Ploeh.AutoFixture.AutoNSubstitute.XML";
      "Src/AutoFoq/bin/Release/Ploeh.AutoFixture.AutoFoq.dll";
      "Src/AutoFoq/bin/Release/Ploeh.AutoFixture.AutoFoq.pdb";
      "Src/AutoFoq/bin/Release/Ploeh.AutoFixture.AutoFoq.XML";
      "Src/AutoFixture.xUnit.net/bin/Release/Ploeh.AutoFixture.Xunit.dll";
      "Src/AutoFixture.xUnit.net/bin/Release/Ploeh.AutoFixture.Xunit.pdb";
      "Src/AutoFixture.xUnit.net/bin/Release/Ploeh.AutoFixture.Xunit.XML";
      "Src/AutoFixture.xUnit.net2/bin/Release/Ploeh.AutoFixture.Xunit2.dll";
      "Src/AutoFixture.xUnit.net2/bin/Release/Ploeh.AutoFixture.Xunit2.pdb";
      "Src/AutoFixture.xUnit.net2/bin/Release/Ploeh.AutoFixture.Xunit2.XML";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.dll";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.pdb";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.XML";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.Addins.dll";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.Addins.pdb";
      "Src/AutoFixture.NUnit2/bin/Release/Ploeh.AutoFixture.NUnit2.Addins.XML";
      "Src/AutoFixture.NUnit3/bin/Release/Ploeh.AutoFixture.NUnit3.dll";
      "Src/AutoFixture.NUnit3/bin/Release/Ploeh.AutoFixture.NUnit3.pdb";
      "Src/AutoFixture.NUnit3/bin/Release/Ploeh.AutoFixture.NUnit3.XML";
      "Src/Idioms/bin/Release/Ploeh.AutoFixture.Idioms.dll";
      "Src/Idioms/bin/Release/Ploeh.AutoFixture.Idioms.pdb";
      "Src/Idioms/bin/Release/Ploeh.AutoFixture.Idioms.XML";
      "Src/Idioms.FsCheck/bin/Release/Ploeh.AutoFixture.Idioms.FsCheck.dll";
      "Src/Idioms.FsCheck/bin/Release/Ploeh.AutoFixture.Idioms.FsCheck.pdb";
      "Src/Idioms.FsCheck/bin/Release/Ploeh.AutoFixture.Idioms.FsCheck.XML";
      nunitToolsFolder @@ "lib/nunit.core.interfaces.dll"
    ]
    let nuGetPackageScripts = !! "NuGet/*.ps1" ++ "NuGet/*.txt" ++ "NuGet/*.pp" |> List.ofSeq
    let releaseFiles = buildOutput @ nuGetPackageScripts

    releaseFiles
    |> CopyFiles releaseFolder
)

Target "CleanNuGetPackages" (fun _ ->
    CleanDir nuGetOutputFolder
)

Target "NuGetPack" (fun _ ->
    let version = buildVersion.nugetVersion
    let nuSpecFiles = !! "NuGet/*.nuspec"

    nuSpecFiles
    |> Seq.iter (fun f -> NuGet (fun p -> { p with Version = version
                                                   WorkingDir = releaseFolder
                                                   OutputPath = nuGetOutputFolder
                                                   SymbolPackage = NugetSymbolPackage.Nuspec }) f)
)

// Starting from NuGet 3.5 it's possible to push package and symbols simultaneously for the custom feeds.
// FAKE API doesn't provide appropriate API, therefore we use NuGet tool directly and pass the necessary parameters.
let publishPackageWithSymbols package packageFeed symbolFeed accessKey =
    // Strip sensitive data from FAKE output
    let replaceAccessKey (text : string) = text.Replace(accessKey, "PRIVATEKEY")

    let originalTracing = enableProcessTracing
    enableProcessTracing <- false

    let defaultParameters = NuGetDefaults()

    let baseArgs = sprintf "push %s \"%s\" -Source %s" package accessKey packageFeed
    let args = match symbolFeed with
               | "" -> baseArgs
               | _  -> sprintf "%s -SymbolSource %s -SymbolApiKey \"%s\"" baseArgs symbolFeed accessKey

    tracefn "%s %s" defaultParameters.ToolPath (replaceAccessKey args) 

    try
        let result = ExecProcess (fun info -> info.FileName <- defaultParameters.ToolPath
                                              info.Arguments <- args)
                                 defaultParameters.TimeOut

        enableProcessTracing <- originalTracing
        if result <> 0 then failwithf "Error during NuGet push. %s %s" defaultParameters.ToolPath args
    with exn ->
        if isNull exn.InnerException then exn.Message else sprintf "%s\r\n%s" exn.Message exn.InnerException.Message
        |> replaceAccessKey
        |> failwith

Target "PublishNuGetPublicOnly" (fun _ ->
    let feed = "https://www.nuget.org/api/v2/package"
    let key = getBuildParam "NuGetPublicKey"

    nuGetPackages
    |> Seq.iter (fun p -> publishPackageWithSymbols p feed "" key)
)

Target "PublishNuGetPrivateOnly" (fun _ ->
    let packageFeed = "https://www.myget.org/F/autofixture/api/v2/package"
    let symbolFeed = "https://www.myget.org/F/autofixture/symbols/api/v2/package"
    let key = getBuildParam "NuGetPrivateKey"

    nuGetPackages
    |> Seq.iter (fun p -> publishPackageWithSymbols p packageFeed symbolFeed key)
)

Target "CompleteBuild"       (fun _ -> ())
Target "PublishNuGetPrivate" (fun _ -> ())
Target "PublishNuGetPublic"  (fun _ -> ())
Target "PublishNuGetAll"     (fun _ -> ())

"CleanVerify"  ==> "CleanAll"
"CleanRelease" ==> "CleanAll"

"CleanReleaseFolder" ==> "Verify"
"CleanAll"           ==> "Verify"

"Verify"                ==> "Build"
"PatchAssemblyVersions" ==> "Build"
"BuildOnly"             ==> "Build"

"Build"    ==> "Test"
"TestOnly" ==> "Test"

"BuildOnly" ==> "TestOnly"

"BuildOnly" ==> "BuildAndTestOnly"
"TestOnly"  ==> "BuildAndTestOnly"

"Test" ==> "CopyToReleaseFolder"

"CleanNuGetPackages"  ==> "NuGetPack"
"CopyToReleaseFolder" ==> "NuGetPack"

"NuGetPack" ==> "CompleteBuild"

"NuGetPack"              ==> "PublishNuGetPublic"
"PublishNuGetPublicOnly" ==> "PublishNuGetPublic"

"NuGetPack"               ==> "PublishNuGetPrivate"
"PublishNuGetPrivateOnly" ==> "PublishNuGetPrivate"

"PublishNuGetPublic"  ==> "PublishNuGetAll"
"PublishNuGetPrivate" ==> "PublishNuGetAll"

RunTargetOrDefault "CompleteBuild"
