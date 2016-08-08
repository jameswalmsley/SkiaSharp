#addin "Cake.Xamarin"
#addin "Cake.XCode"
#addin nuget:?package=Cake.FileHelpers

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

var TARGET = Argument ("t", Argument ("target", Argument ("Target", "Default")));

var NuGetSources = new [] { "https://api.nuget.org/v3/index.json", MakeAbsolute (Directory ("./output")).FullPath };
var NugetToolPath = GetToolPath ("../nuget.exe");
var XamarinComponentToolPath = GetToolPath ("../xamarin-component.exe");
var CakeToolPath = GetToolPath ("Cake.exe");
var NUnitConsoleToolPath = GetToolPath ("../NUnit.Console/tools/nunit3-console.exe");
var GenApiToolPath = GetToolPath ("../genapi.exe");
var MDocPath = GetMDocPath ();

DirectoryPath ROOT_PATH = MakeAbsolute(Directory("."));
DirectoryPath DEPOT_PATH = MakeAbsolute(ROOT_PATH.Combine("depot_tools"));
DirectoryPath SKIA_PATH = MakeAbsolute(ROOT_PATH.Combine("skia"));

////////////////////////////////////////////////////////////////////////////////////////////////////
// TOOLS & FUNCTIONS - the bits to make it all work
////////////////////////////////////////////////////////////////////////////////////////////////////

var SetEnvironmentVariable = new Action<string, string> ((name, value) => {
    Information ("Setting Environment Variable {0} to {1}", name, value);
    Environment.SetEnvironmentVariable (name, value, EnvironmentVariableTarget.Process);
});
var AppendEnvironmentVariable = new Action<string, string> ((name, value) => {
    var old = EnvironmentVariable (name);
    var sep = IsRunningOnWindows () ? ';' : ':';

    if (!old.ToUpper ().Split (sep).Contains (value.ToUpper ())) {
        Information ("Adding {0} to Environment Variable {1}", value, name);
        value += sep + old;
        SetEnvironmentVariable (name, value);
    }
});
void ListEnvironmentVariables ()
{
    Information ("Environment Variables:");
    foreach (var envVar in EnvironmentVariables ()) {
        Information ("\tKey: {0}\tValue: \"{1}\"", envVar.Key, envVar.Value);
    }
}

FilePath GetToolPath (FilePath toolPath)
{
	var appRoot = Context.Environment.GetApplicationRoot ();
 	var appRootExe = appRoot.CombineWithFilePath (toolPath);
 	if (FileExists (appRootExe))
 		return appRootExe;
    throw new FileNotFoundException ("Unable to find tool: " + appRootExe);
}

FilePath GetMDocPath ()
{
    FilePath mdocPath;
    if (IsRunningOnUnix ()) {
        mdocPath = "/Library/Frameworks/Mono.framework/Versions/Current/bin/mdoc";
    } else {
        mdocPath = GetToolPath ("../mdoc/mdoc.exe");
    }
    if (!FileExists (mdocPath)) {
        mdocPath = "mdoc";
    }
    return mdocPath;
}

var RunNuGetRestore = new Action<FilePath> ((solution) =>
{
    NuGetRestore (solution, new NuGetRestoreSettings {
        ToolPath = NugetToolPath,
        Source = NuGetSources,
        Verbosity = NuGetVerbosity.Detailed
    });
});

var RunGyp = new Action<string, string> ((defines, generators) =>
{
    SetEnvironmentVariable ("GYP_GENERATORS", generators);
    SetEnvironmentVariable ("GYP_DEFINES", defines);

    Information ("Running 'sync-and-gyp'...");
    Information ("\tGYP_GENERATORS = " + EnvironmentVariable ("GYP_GENERATORS"));
    Information ("\tGYP_DEFINES = " + EnvironmentVariable ("GYP_DEFINES"));

    var result = StartProcess ("python", new ProcessSettings {
        Arguments = SKIA_PATH.CombineWithFilePath("bin/sync-and-gyp").FullPath,
        WorkingDirectory = SKIA_PATH.FullPath,
    });
    if (result != 0) {
        throw new Exception ("sync-and-gyp failed with error: " + result);
    }
});

var RunInstallNameTool = new Action<DirectoryPath, string, string, FilePath> ((directory, oldName, newName, library) =>
{
    if (!IsRunningOnUnix ()) {
        throw new InvalidOperationException ("install_name_tool is only available on Unix.");
    }

	StartProcess ("install_name_tool", new ProcessSettings {
		Arguments = string.Format("-change {0} {1} \"{2}\"", oldName, newName, library),
		WorkingDirectory = directory,
	});
});

var RunLipo = new Action<DirectoryPath, FilePath, FilePath[]> ((directory, output, inputs) =>
{
    if (!IsRunningOnUnix ()) {
        throw new InvalidOperationException ("lipo is only available on Unix.");
    }

    var dir = directory.CombineWithFilePath (output).GetDirectory ();
    if (!DirectoryExists (dir)) {
        CreateDirectory (dir);
    }

	var inputString = string.Join(" ", inputs.Select (i => string.Format ("\"{0}\"", i)));
	StartProcess ("lipo", new ProcessSettings {
		Arguments = string.Format("-create -output \"{0}\" {1}", output, inputString),
		WorkingDirectory = directory,
	});
});

var PackageNuGet = new Action<FilePath, DirectoryPath> ((nuspecPath, outputPath) =>
{
	if (!DirectoryExists (outputPath)) {
		CreateDirectory (outputPath);
	}

    NuGetPack (nuspecPath, new NuGetPackSettings {
        Verbosity = NuGetVerbosity.Detailed,
        OutputDirectory = outputPath,
        BasePath = "./",
        ToolPath = NugetToolPath
    });
});

var RunTests = new Action<FilePath> ((testAssembly) =>
{
    var dir = testAssembly.GetDirectory ();
    var result = StartProcess (NUnitConsoleToolPath, new ProcessSettings {
        Arguments = string.Format ("\"{0}\" --work=\"{1}\"", testAssembly, dir),
    });

    if (result != 0) {
        throw new Exception ("NUnit test failed with error: " + result);
    }
});

var RunMdocUpdate = new Action<FilePath, DirectoryPath> ((assembly, docsRoot) =>
{
    StartProcess (MDocPath, new ProcessSettings {
        Arguments = string.Format ("update --delete --out=\"{0}\" \"{1}\"", docsRoot, assembly),
    });
});

var RunMdocMSXml = new Action<DirectoryPath, FilePath> ((docsRoot, output) =>
{
    StartProcess (MDocPath, new ProcessSettings {
        Arguments = string.Format ("export-msxdoc --out=\"{0}\" \"{1}\"", output, docsRoot),
    });
});

var RunMdocAssemble = new Action<DirectoryPath, FilePath> ((docsRoot, output) =>
{
    StartProcess (MDocPath, new ProcessSettings {
        Arguments = string.Format ("assemble --out=\"{0}\" \"{1}\"", output, docsRoot),
    });
});

var ProcessSolutionProjects = new Action<FilePath, Action<string, FilePath>> ((solutionFilePath, process) => {
    var solutionFile = MakeAbsolute (solutionFilePath).FullPath;
    foreach (var line in FileReadLines (solutionFile)) {
        var match = Regex.Match (line, @"Project\(""(?<type>.*)""\) = ""(?<name>.*)"", ""(?<path>.*)"", "".*""");
        if (match.Success && match.Groups ["type"].Value == "{8BC9CEB8-8B4A-11D0-8D11-00A0C91BC942}") {
            var path = match.Groups["path"].Value;
            var projectFilePath = MakeAbsolute (solutionFilePath.GetDirectory ().CombineWithFilePath (path));
            Information ("Processing project file: " + projectFilePath);
            process (match.Groups["name"].Value, projectFilePath);
        }
    }
});

var MSBuildNS = (XNamespace) "http://schemas.microsoft.com/developer/msbuild/2003";

var SetXValue = new Action<XElement, string, string> ((root, element, value) => {
    var node = root.Element (MSBuildNS + element);
    if (node == null)
        root.Add (new XElement (MSBuildNS + element, value));
    else
        node.Value = value;
});
var AddXValue = new Action<XElement, string, string> ((root, element, value) => {
    var node = root.Element (MSBuildNS + element);
    if (node == null)
        root.Add (new XElement (MSBuildNS + element, value));
    else if (!node.Value.Contains (value))
        node.Value += value;
});
var RemoveXValue = new Action<XElement, string, string> ((root, element, value) => {
    var node = root.Element (MSBuildNS + element);
    if (node != null)
        node.Value = node.Value.Replace (value, string.Empty);
});
var SetXValues = new Action<XElement, string[], string, string> ((root, parents, element, value) => {
    IEnumerable<XElement> nodes = new [] { root };
    foreach (var p in parents) {
        nodes = nodes.Elements (MSBuildNS + p);
    }
    foreach (var n in nodes) {
        SetXValue (n, element, value);
    }
});
var AddXValues = new Action<XElement, string[], string, string> ((root, parents, element, value) => {
    IEnumerable<XElement> nodes = new [] { root };
    foreach (var p in parents) {
        nodes = nodes.Elements (MSBuildNS + p);
    }
    foreach (var n in nodes) {
        AddXValue (n, element, value);
    }
});
var RemoveXValues = new Action<XElement, string[], string, string> ((root, parents, element, value) => {
    IEnumerable<XElement> nodes = new [] { root };
    foreach (var p in parents) {
        nodes = nodes.Elements (MSBuildNS + p);
    }
    foreach (var n in nodes) {
        RemoveXValue (n, element, value);
    }
});
var RemoveFileReference = new Action<XElement, string> ((root, filename) => {
    var element = root
        .Elements (MSBuildNS + "ItemGroup")
        .Elements (MSBuildNS + "ClCompile")
        .Where (e => e.Attribute ("Include") != null)
        .Where (e => e.Attribute ("Include").Value.Contains (filename))
        .FirstOrDefault ();
    if (element != null) {
        element.Remove ();
    }
});
var AddFileReference = new Action<XElement, string> ((root, filename) => {
    var element = root
        .Elements (MSBuildNS + "ItemGroup")
        .Elements (MSBuildNS + "ClCompile")
        .Where (e => e.Attribute ("Include") != null)
        .Where (e => e.Attribute ("Include").Value.Contains (filename))
        .FirstOrDefault ();
    if (element == null) {
        root.Elements (MSBuildNS + "ItemGroup")
            .Elements (MSBuildNS + "ClCompile")
            .Last ()
            .Parent
            .Add (new XElement (MSBuildNS + "ClCompile", new XAttribute ("Include", filename)));
    }
});

// find a better place for this / or fix the path issue
var VisualStudioPathFixup = new Action (() => {
    var props = SKIA_PATH.CombineWithFilePath ("out/gyp/libjpeg-turbo.props").FullPath;
    var xdoc = XDocument.Load (props);
    var temp = xdoc.Root
        .Elements (MSBuildNS + "ItemDefinitionGroup")
        .Elements (MSBuildNS + "assemble")
        .Elements (MSBuildNS + "CommandLineTemplate")
        .Single ();
    var newInclude = SKIA_PATH.Combine ("third_party/externals/libjpeg-turbo/win/").FullPath;
    if (!temp.Value.Contains (newInclude)) {
        temp.Value += " \"-I" + newInclude + "\"";
        xdoc.Save (props);
    }
});

var InjectCompatibilityExternals = new Action<bool> ((inject) => {
    // some methods don't yet exist, so we must add the compat layer to them.
    // we need this as we can't modify the third party files
    // all we do is insert our header before all the others
    var compatHeader = "native-builds/src/WinRTCompat.h";
    var compatSource = "native-builds/src/WinRTCompat.c";
    var files = new Dictionary<FilePath, string> {
        { "skia/third_party/externals/dng_sdk/source/dng_string.cpp", "#if qWinOS" },
        { "skia/third_party/externals/dng_sdk/source/dng_utils.cpp", "#if qWinOS" },
        { "skia/third_party/externals/dng_sdk/source/dng_pthread.cpp", "#if qWinOS" },
        { "skia/third_party/externals/zlib/deflate.c", "#include <assert.h>" },
    };
    foreach (var filePair in files) {
        var file = filePair.Key;
        var root = string.Join ("/", file.GetDirectory().Segments.Select (x => ".."));
        var include = "#include \"" + root + "/" + compatHeader + "\"";

        var contents = FileReadLines (file).ToList ();
        var index = contents.IndexOf (include);
        if (index == -1 && inject) {
            if (string.IsNullOrEmpty (filePair.Value)) {
                contents.Insert (0, include);
            } else {
                contents.Insert (contents.IndexOf (filePair.Value), include);
            }
            FileWriteLines (file, contents.ToArray ());
        } else if (index != -1 && !inject) {
            int idx = 0;
            if (string.IsNullOrEmpty (filePair.Value)) {
                idx = 0;
            } else {
                idx = contents.IndexOf (filePair.Value) - 1;
            }
            if (contents [idx] == include) {
                contents.RemoveAt (idx);
            }
            FileWriteLines (file, contents.ToArray ());
        }
    }
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// EXTERNALS - the native C and C++ libraries
////////////////////////////////////////////////////////////////////////////////////////////////////

// this builds all the externals
Task ("externals")
    .IsDependentOn ("externals-genapi")
    .IsDependentOn ("externals-native")
    .Does (() =>
{
});
// this builds the native C and C++ externals
Task ("externals-native")
    .IsDependentOn ("externals-uwp")
    .IsDependentOn ("externals-windows")
    .Does (() =>
{
    // copy all the native files into the output
    CopyDirectory ("./native-builds/lib/", "./output/native/");

    // copy the non-embedded native files into the output
    if (IsRunningOnWindows ()) {
        if (!DirectoryExists ("./output/windows/x86")) CreateDirectory ("./output/windows/x86");
        if (!DirectoryExists ("./output/windows/x64")) CreateDirectory ("./output/windows/x64");
        CopyFileToDirectory ("./native-builds/lib/windows/x86/libSkiaSharp.dll", "./output/windows/x86/");
        CopyFileToDirectory ("./native-builds/lib/windows/x86/libSkiaSharp.pdb", "./output/windows/x86/");
        CopyFileToDirectory ("./native-builds/lib/windows/x64/libSkiaSharp.dll", "./output/windows/x64/");
        CopyFileToDirectory ("./native-builds/lib/windows/x64/libSkiaSharp.pdb", "./output/windows/x64/");
        if (!DirectoryExists ("./output/uwp/x86")) CreateDirectory ("./output/uwp/x86");
        if (!DirectoryExists ("./output/uwp/x64")) CreateDirectory ("./output/uwp/x64");
        if (!DirectoryExists ("./output/uwp/arm")) CreateDirectory ("./output/uwp/arm");
        CopyFileToDirectory ("./native-builds/lib/uwp/x86/libSkiaSharp.dll", "./output/uwp/x86/");
        CopyFileToDirectory ("./native-builds/lib/uwp/x86/libSkiaSharp.pdb", "./output/uwp/x86/");
        CopyFileToDirectory ("./native-builds/lib/uwp/x64/libSkiaSharp.dll", "./output/uwp/x64/");
        CopyFileToDirectory ("./native-builds/lib/uwp/x64/libSkiaSharp.pdb", "./output/uwp/x64/");
        CopyFileToDirectory ("./native-builds/lib/uwp/arm/libSkiaSharp.dll", "./output/uwp/arm/");
        CopyFileToDirectory ("./native-builds/lib/uwp/arm/libSkiaSharp.pdb", "./output/uwp/arm/");
    }
    if (IsRunningOnUnix ()) {
        if (!DirectoryExists ("./output/linux")) CreateDirectory ("./output/linux");
        if (!DirectoryExists ("./output/mac")) CreateDirectory ("./output/mac");
        CopyFileToDirectory ("./native-builds/src/libSkiaSharp.so", "./output/linux/");
        CopyFileToDirectory ("./native-builds/src/libSkiaSharp.so", "./output/mac/");
    }
});
// this builds the managed PCL external
Task ("externals-genapi")
    .Does (() =>
{
    // build the dummy project
    DotNetBuild ("binding/SkiaSharp.Generic.sln", c => {
        c.Configuration = "Release";
        c.Properties ["Platform"] = new [] { "\"Any CPU\"" };
    });

    // generate the PCL
    FilePath input = "binding/SkiaSharp.Generic/bin/Release/SkiaSharp.dll";
    var libPath = "/Library/Frameworks/Mono.framework/Versions/Current/lib/mono/4.5/,.";
    StartProcess (GenApiToolPath, new ProcessSettings {
        Arguments = string.Format("-libPath:{2} -out \"{0}\" \"{1}\"", input.GetFilename () + ".cs", input.GetFilename (), libPath),
        WorkingDirectory = input.GetDirectory ().FullPath,
    });
    CopyFile ("binding/SkiaSharp.Generic/bin/Release/SkiaSharp.dll.cs", "binding/SkiaSharp.Portable/SkiaPortable.cs");
});
// this builds the native C and C++ externals for Windows
Task ("externals-windows")
    .WithCriteria (IsRunningOnWindows ())
    .WithCriteria (
        !FileExists ("native-builds/lib/windows/x86/libSkiaSharp.dll") ||
        !FileExists ("native-builds/lib/windows/x64/libSkiaSharp.dll"))
    .Does (() =>
{
    var buildArch = new Action<string, string, string> ((platform, skiaArch, dir) => {
        RunGyp ("skia_arch_type='" + skiaArch + "'", "ninja,msvs");
        VisualStudioPathFixup ();
        DotNetBuild ("native-builds/libSkiaSharp_windows/libSkiaSharp_" + dir + ".sln", c => {
            c.Configuration = "Release";
            c.Properties ["Platform"] = new [] { platform };
        });
        if (!DirectoryExists ("native-builds/lib/windows/" + dir)) CreateDirectory ("native-builds/lib/windows/" + dir);
        CopyFileToDirectory ("native-builds/libSkiaSharp_windows/Release/libSkiaSharp.lib", "native-builds/lib/windows/" + dir);
        CopyFileToDirectory ("native-builds/libSkiaSharp_windows/Release/libSkiaSharp.dll", "native-builds/lib/windows/" + dir);
        CopyFileToDirectory ("native-builds/libSkiaSharp_windows/Release/libSkiaSharp.pdb", "native-builds/lib/windows/" + dir);
    });

    // set up the gyp environment variables
    AppendEnvironmentVariable ("PATH", DEPOT_PATH.FullPath);

    buildArch ("Win32", "x86", "x86");
    buildArch ("x64", "x86_64", "x64");
});
// this builds the native C and C++ externals for Windows UWP
Task ("externals-uwp")
    .WithCriteria (IsRunningOnWindows ())
    .WithCriteria (
        !FileExists ("native-builds/lib/uwp/ARM/libSkiaSharp.dll") ||
        !FileExists ("native-builds/lib/uwp/x86/libSkiaSharp.dll") ||
        !FileExists ("native-builds/lib/uwp/x64/libSkiaSharp.dll"))
    .Does (() =>
{
    var convertDesktopToUWP = new Action<FilePath, string> ((projectFilePath, platform) => {
        //
        // TODO: the stuff in this block must be moved into the gyp files !!
        //

        var projectFile = MakeAbsolute (projectFilePath).FullPath;
        var xdoc = XDocument.Load (projectFile);

        var configType = xdoc.Root
            .Elements (MSBuildNS + "PropertyGroup")
            .Elements (MSBuildNS + "ConfigurationType")
            .Select (e => e.Value)
            .FirstOrDefault ();
        if (configType != "StaticLibrary") {
            // skip over "Utility" projects as they aren't actually
            // library projects, but intermediate build steps.
            return;
        } else {
            // special case for ARM, gyp does not yet have ARM,
            // so it defaults to Win32
            // update and reload
            if (platform.ToUpper () == "ARM") {
                ReplaceTextInFiles (projectFile, "Win32", "ARM");
                xdoc = XDocument.Load (projectFile);
            }
        }

        var rootNamespace = xdoc.Root
            .Elements (MSBuildNS + "PropertyGroup")
            .Elements (MSBuildNS + "RootNamespace")
            .Select (e => e.Value)
            .FirstOrDefault ();
        var globals = xdoc.Root
            .Elements (MSBuildNS + "PropertyGroup")
            .Where (e => e.Attribute ("Label") != null && e.Attribute ("Label").Value == "Globals")
            .Single ();

        globals.Elements (MSBuildNS + "WindowsTargetPlatformVersion").Remove ();
        SetXValue (globals, "Keyword", "StaticLibrary");
        SetXValue (globals, "AppContainerApplication", "true");
        SetXValue (globals, "ApplicationType", "Windows Store");
        SetXValue (globals, "WindowsTargetPlatformVersion", "10.0.10586.0");
        SetXValue (globals, "WindowsTargetPlatformMinVersion", "10.0.10240.0");
        SetXValue (globals, "ApplicationTypeRevision", "10.0");
        SetXValue (globals, "DefaultLanguage", "en-US");

        var properties = xdoc.Root
            .Elements (MSBuildNS + "PropertyGroup")
            .Elements (MSBuildNS + "LinkIncremental")
            .First ()
            .Parent;
        SetXValue (properties, "GenerateManifest","false");
        SetXValue (properties, "IgnoreImportLibrary","false");

        SetXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "ClCompile" }, "CompileAsWinRT", "false");
        AddXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "ClCompile" }, "PreprocessorDefinitions", ";SK_BUILD_FOR_WINRT;WINAPI_FAMILY=WINAPI_FAMILY_APP;");
        AddXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "ClCompile" }, "PreprocessorDefinitions", ";SK_HAS_DWRITE_1_H;SK_HAS_DWRITE_2_H;");
        // if (platform.ToUpper () == "ARM") {
        //     AddXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "ClCompile" }, "PreprocessorDefinitions", ";__ARM_NEON;__ARM_NEON__;");
        // }
        AddXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "ClCompile" }, "DisableSpecificWarnings", ";4146;4703;");
        SetXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "Link" }, "SubSystem", "Console");
        SetXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "Link" }, "IgnoreAllDefaultLibraries", "false");
        SetXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "Link" }, "GenerateWindowsMetadata", "false");

        xdoc.Root
            .Elements (MSBuildNS + "ItemDefinitionGroup")
            .Elements (MSBuildNS + "Link")
            .Elements (MSBuildNS + "AdditionalDependencies")
            .Remove ();

        // remove sfntly as this is not supported for winrt
        RemoveXValues (xdoc.Root, new [] { "ItemDefinitionGroup", "ClCompile" }, "PreprocessorDefinitions", "SK_SFNTLY_SUBSETTER=\"font_subsetter.h\"");

        if (rootNamespace == "ports") {
            RemoveFileReference (xdoc.Root, "SkFontHost_win.cpp");
        } else if (rootNamespace == "skgpu" ) {
            // GL is not available to WinRT
            RemoveFileReference (xdoc.Root, "GrGLCreateNativeInterface_none.cpp");
            AddFileReference (xdoc.Root, @"..\..\src\gpu\gl\GrGLCreateNativeInterface_none.cpp");
            RemoveFileReference (xdoc.Root, "GrGLCreateNativeInterface_win.cpp");
            RemoveFileReference (xdoc.Root, "SkCreatePlatformGLContext_win.cpp");
        } else if (rootNamespace == "utils" ) {
            // GL is not available to WinRT
            RemoveFileReference (xdoc.Root, "SkWGL.h");
            RemoveFileReference (xdoc.Root, "SkWGL_win.cpp");
        }

        xdoc.Save (projectFile);
    });

    var buildArch = new Action<string, string> ((platform, arch) => {
        CleanDirectories ("native-builds/libSkiaSharp_uwp/" + arch);
        CleanDirectories ("native-builds/libSkiaSharp_uwp/Release");
        CleanDirectories ("native-builds/libSkiaSharp_uwp/Generated Files");
        ProcessSolutionProjects ("native-builds/libSkiaSharp_uwp/libSkiaSharp_" + arch + ".sln", (projectName, projectPath) => {
            if (projectName != "libSkiaSharp")
                convertDesktopToUWP (projectPath, platform);
        });
        InjectCompatibilityExternals (true);
        VisualStudioPathFixup ();
        DotNetBuild ("native-builds/libSkiaSharp_uwp/libSkiaSharp_" + arch + ".sln", c => {
            c.Configuration = "Release";
            c.Properties ["Platform"] = new [] { platform };
        });
        if (!DirectoryExists ("native-builds/lib/uwp/" + arch)) CreateDirectory ("native-builds/lib/uwp/" + arch);
        CopyFileToDirectory ("native-builds/libSkiaSharp_uwp/Release/libSkiaSharp.lib", "native-builds/lib/uwp/" + arch);
        CopyFileToDirectory ("native-builds/libSkiaSharp_uwp/Release/libSkiaSharp.dll", "native-builds/lib/uwp/" + arch);
        CopyFileToDirectory ("native-builds/libSkiaSharp_uwp/Release/libSkiaSharp.pdb", "native-builds/lib/uwp/" + arch);
    });

    // set up the gyp environment variables
    AppendEnvironmentVariable ("PATH", DEPOT_PATH.FullPath);

    RunGyp ("skia_arch_type='x86_64'", "ninja,msvs");
    buildArch ("x64", "x64");

    RunGyp ("skia_arch_type='x86'", "ninja,msvs");
    buildArch ("Win32", "x86");

    RunGyp ("skia_arch_type='arm' arm_version=7 arm_neon=0", "ninja,msvs");
    buildArch ("ARM", "arm");
});
// this builds the native C and C++ externals for Linux

Task ("externals-linux")
    .WithCriteria (IsRunningOnUnix ())
    .WithCriteria (
        !FileExists ("native-builds/lib/linux/libSkiaSharp.so"))
    .Does (() =>
{
    var buildArch = new Action<string, string> ((arch, skiaArch) => {
        RunGyp ("skia_arch_type='" + skiaArch + "'", "ninja");

        /*XCodeBuild (new XCodeBuildSettings {
            Project = "native-builds/libSkiaSharp_osx/libSkiaSharp.xcodeproj",
            Target = "libSkiaSharp",
            Sdk = "macosx",
            Arch = arch,
            Configuration = "Release",
        });*/
        if (!DirectoryExists ("native-builds/lib/linux/" + arch)) {
            CreateDirectory ("native-builds/lib/linux/" + arch);
        }
        CopyDirectory ("native-builds/libSkiaSharp_linux/build/Release/", "native-builds/lib/linux/" + arch);
        RunInstallNameTool ("native-builds/lib/linux/" + arch, "lib/libSkiaSharp.so", "@loader_path/libSkiaSharp.so", "libSkiaSharp.so");
    });

    // set up the gyp environment variables
    AppendEnvironmentVariable ("PATH", DEPOT_PATH.FullPath);

    buildArch ("i386", "x86");
    buildArch ("x86_64", "x86_64");

    // create the fat dylib
    RunLipo ("native-builds/lib/linux/", "libSkiaSharp.so", new [] {
        (FilePath) "i386/libSkiaSharp.so",
        (FilePath) "x86_64/libSkiaSharp.so"
    });
});

// this builds the native C and C++ externals for iOS
Task ("externals-ios")
    .WithCriteria (IsRunningOnUnix ())
    .WithCriteria (
        !FileExists ("native-builds/lib/ios/libSkiaSharp.framework/libSkiaSharp"))
    .Does (() =>
{
    var buildArch = new Action<string, string> ((sdk, arch) => {
        XCodeBuild (new XCodeBuildSettings {
            Project = "native-builds/libSkiaSharp_ios/libSkiaSharp.xcodeproj",
            Target = "libSkiaSharp",
            Sdk = sdk,
            Arch = arch,
            Configuration = "Release",
        });
        if (!DirectoryExists ("native-builds/lib/ios/" + arch)) {
            CreateDirectory ("native-builds/lib/ios/" + arch);
        }
        CopyDirectory ("native-builds/libSkiaSharp_ios/build/Release-" + sdk, "native-builds/lib/ios/" + arch);
    });

    // set up the gyp environment variables
    AppendEnvironmentVariable ("PATH", DEPOT_PATH.FullPath);

    RunGyp ("skia_os='ios' skia_arch_type='arm' armv7=1 arm_neon=0", "ninja,xcode");

    buildArch ("iphonesimulator", "i386");
    buildArch ("iphonesimulator", "x86_64");
    buildArch ("iphoneos", "armv7");
    buildArch ("iphoneos", "armv7s");
    buildArch ("iphoneos", "arm64");

    // create the fat framework
    CopyDirectory ("native-builds/lib/ios/armv7/libSkiaSharp.framework/", "native-builds/lib/ios/libSkiaSharp.framework/");
    DeleteFile ("native-builds/lib/ios/libSkiaSharp.framework/libSkiaSharp");
    RunLipo ("native-builds/lib/ios/", "libSkiaSharp.framework/libSkiaSharp", new [] {
        (FilePath) "i386/libSkiaSharp.framework/libSkiaSharp",
        (FilePath) "x86_64/libSkiaSharp.framework/libSkiaSharp",
        (FilePath) "armv7/libSkiaSharp.framework/libSkiaSharp",
        (FilePath) "armv7s/libSkiaSharp.framework/libSkiaSharp",
        (FilePath) "arm64/libSkiaSharp.framework/libSkiaSharp"
    });
});
// this builds the native C and C++ externals for tvOS
Task ("externals-tvos")
    .WithCriteria (IsRunningOnUnix ())
    .WithCriteria (
        !FileExists ("native-builds/lib/tvos/libSkiaSharp.framework/libSkiaSharp"))
    .Does (() =>
{
    var convertIOSToTVOS = new Action (() => {
        var glob = "./skia/out/gyp/*.xcodeproj/project.pbxproj";
        ReplaceTextInFiles (glob, "SDKROOT = iphoneos;", "SDKROOT = appletvos;");
        ReplaceTextInFiles (glob, "IPHONEOS_DEPLOYMENT_TARGET = 9.2;", "TVOS_DEPLOYMENT_TARGET = 9.1;");
        ReplaceTextInFiles (glob, "TARGETED_DEVICE_FAMILY = \"1,2\";", "TARGETED_DEVICE_FAMILY = 3;");
        ReplaceTextInFiles (glob, "\"CODE_SIGN_IDENTITY[sdk=iphoneos*]\" = \"iPhone Developer\";", "");
    });

    var buildArch = new Action<string, string> ((sdk, arch) => {
        XCodeBuild (new XCodeBuildSettings {
            Project = "native-builds/libSkiaSharp_tvos/libSkiaSharp.xcodeproj",
            Target = "libSkiaSharp",
            Sdk = sdk,
            Arch = arch,
            Configuration = "Release",
        });
        if (!DirectoryExists ("native-builds/lib/tvos/" + arch)) {
            CreateDirectory ("native-builds/lib/tvos/" + arch);
        }
        CopyDirectory ("native-builds/libSkiaSharp_tvos/build/Release-" + sdk, "native-builds/lib/tvos/" + arch);
    });

    // set up the gyp environment variables
    AppendEnvironmentVariable ("PATH", DEPOT_PATH.FullPath);

    RunGyp ("skia_os='ios' skia_arch_type='arm' armv7=1 arm_neon=0", "ninja,xcode");
    convertIOSToTVOS();

    buildArch ("appletvsimulator", "x86_64");
    buildArch ("appletvos", "arm64");

    // create the fat framework
    CopyDirectory ("native-builds/lib/tvos/arm64/libSkiaSharp.framework/", "native-builds/lib/tvos/libSkiaSharp.framework/");
    DeleteFile ("native-builds/lib/tvos/libSkiaSharp.framework/libSkiaSharp");
    RunLipo ("native-builds/lib/tvos/", "libSkiaSharp.framework/libSkiaSharp", new [] {
        (FilePath) "x86_64/libSkiaSharp.framework/libSkiaSharp",
        (FilePath) "arm64/libSkiaSharp.framework/libSkiaSharp"
    });
});
// this builds the native C and C++ externals for Android
Task ("externals-android")
    .WithCriteria (IsRunningOnUnix ())
    .WithCriteria (
        !FileExists ("native-builds/lib/android/x86/libSkiaSharp.so") ||
        !FileExists ("native-builds/lib/android/x86_64/libSkiaSharp.so") ||
        !FileExists ("native-builds/lib/android/armeabi-v7a/libSkiaSharp.so") ||
        !FileExists ("native-builds/lib/android/arm64-v8a/libSkiaSharp.so"))
    .Does (() =>
{
    var ANDROID_HOME = EnvironmentVariable ("ANDROID_HOME") ?? EnvironmentVariable ("HOME") + "/Library/Developer/Xamarin/android-sdk-macosx";
    var ANDROID_SDK_ROOT = EnvironmentVariable ("ANDROID_SDK_ROOT") ?? ANDROID_HOME;
    var ANDROID_NDK_HOME = EnvironmentVariable ("ANDROID_NDK_HOME") ?? EnvironmentVariable ("HOME") + "/Library/Developer/Xamarin/android-ndk";

    var buildArch = new Action<string, string> ((arch, folder) => {
        StartProcess (SKIA_PATH.CombineWithFilePath ("platform_tools/android/bin/android_ninja").FullPath, new ProcessSettings {
            Arguments = "-d " + arch + " skia_lib pdf sfntly icuuc",
            WorkingDirectory = SKIA_PATH.FullPath,
        });
    });

    // set up the gyp environment variables
    AppendEnvironmentVariable ("PATH", DEPOT_PATH.FullPath);
    SetEnvironmentVariable ("GYP_DEFINES", "");
    SetEnvironmentVariable ("GYP_GENERATORS", "");
    SetEnvironmentVariable ("BUILDTYPE", "Release");
    SetEnvironmentVariable ("ANDROID_HOME", ANDROID_HOME);
    SetEnvironmentVariable ("ANDROID_SDK_ROOT", ANDROID_SDK_ROOT);
    SetEnvironmentVariable ("ANDROID_NDK_HOME", ANDROID_NDK_HOME);

    SetEnvironmentVariable ("GYP_DEFINES", "");
    buildArch ("x86", "x86");
    SetEnvironmentVariable ("GYP_DEFINES", "");
    buildArch ("x86_64", "x86_64");
    SetEnvironmentVariable ("GYP_DEFINES", "arm_neon=1 arm_version=7");
    buildArch ("arm_v7_neon", "armeabi-v7a");
    SetEnvironmentVariable ("GYP_DEFINES", "arm_neon=0 arm_version=8");
    buildArch ("arm64", "arm64-v8a");

    var ndkbuild = MakeAbsolute (Directory (ANDROID_NDK_HOME)).CombineWithFilePath ("ndk-build").FullPath;
    StartProcess (ndkbuild, new ProcessSettings {
        Arguments = "",
        WorkingDirectory = ROOT_PATH.Combine ("native-builds/libSkiaSharp_android").FullPath,
    });

    foreach (var folder in new [] { "x86", "x86_64", "armeabi-v7a", "arm64-v8a" }) {
        if (!DirectoryExists ("native-builds/lib/android/" + folder)) {
            CreateDirectory ("native-builds/lib/android/" + folder);
        }
        CopyFileToDirectory ("native-builds/libSkiaSharp_android/libs/" + folder + "/libSkiaSharp.so", "native-builds/lib/android/" + folder);
    }
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// LIBS - the managed C# libraries
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("libs")
    .IsDependentOn ("libs-base")
    .IsDependentOn ("libs-windows")
    .IsDependentOn ("libs-linux")
    .Does (() =>
{
});
Task ("libs-base")
    .Does (() =>
{
    // set the SHA on the assembly info
    var sha = EnvironmentVariable ("GIT_COMMIT") ?? string.Empty;
    if (!string.IsNullOrEmpty (sha) && sha.Length >= 6) {
        sha = sha.Substring (0, 6);
        Information ("Setting Git SHA to {0}.", sha);
        ReplaceTextInFiles ("./binding/SkiaSharp/Properties/SkiaSharpAssemblyInfo.cs", "{GIT_SHA}", sha);
    }
});
Task ("libs-windows")
    .WithCriteria (IsRunningOnWindows ())
    .IsDependentOn ("externals")
    .IsDependentOn ("libs-base")
    .Does (() =>
{
    // build
    RunNuGetRestore ("binding/SkiaSharp.Windows.sln");
    DotNetBuild ("binding/SkiaSharp.Windows.sln", c => {
        c.Configuration = "Release";
    });

    if (!DirectoryExists ("./output/portable/")) CreateDirectory ("./output/portable/");
    if (!DirectoryExists ("./output/windows/")) CreateDirectory ("./output/windows/");
    if (!DirectoryExists ("./output/uwp/")) CreateDirectory ("./output/uwp/");

    // copy build output
    CopyFileToDirectory ("./binding/SkiaSharp.Portable/bin/Release/SkiaSharp.dll", "./output/portable/");
    CopyFileToDirectory ("./binding/SkiaSharp.Desktop/bin/Release/SkiaSharp.dll", "./output/windows/");
    CopyFileToDirectory ("./binding/SkiaSharp.Desktop/bin/Release/SkiaSharp.pdb", "./output/windows/");
    CopyFileToDirectory ("./binding/SkiaSharp.Desktop/bin/Release/SkiaSharp.dll.config", "./output/windows/");
    CopyFileToDirectory ("./binding/SkiaSharp.Desktop/bin/Release/SkiaSharp.Desktop.targets", "./output/windows/");
    CopyFileToDirectory ("./binding/SkiaSharp.UWP/bin/Release/SkiaSharp.dll", "./output/uwp/");
    CopyFileToDirectory ("./binding/SkiaSharp.UWP/bin/Release/SkiaSharp.pdb", "./output/uwp/");
    CopyFileToDirectory ("./binding/SkiaSharp.UWP/bin/Release/SkiaSharp.pri", "./output/uwp/");
    CopyFileToDirectory ("./binding/SkiaSharp.UWP/bin/Release/SkiaSharp.UWP.targets", "./output/uwp/");
});
Task ("libs-linux")
    .WithCriteria (IsRunningOnUnix ())
    .IsDependentOn ("libs-base")
    .Does (() =>
{
    // build
    RunNuGetRestore ("binding/SkiaSharp.Mac.sln");
    DotNetBuild ("binding/SkiaSharp.Mac.sln", c => {
        c.Configuration = "Release";
    });

    if (!DirectoryExists ("./output/android/")) CreateDirectory ("./output/android/");
    if (!DirectoryExists ("./output/ios/")) CreateDirectory ("./output/ios/");
    if (!DirectoryExists ("./output/tvos/")) CreateDirectory ("./output/tvos/");
    if (!DirectoryExists ("./output/osx/")) CreateDirectory ("./output/osx/");
    if (!DirectoryExists ("./output/portable/")) CreateDirectory ("./output/portable/");
    if (!DirectoryExists ("./output/mac/")) CreateDirectory ("./output/mac/");

    // copy build output
    CopyFileToDirectory ("./binding/SkiaSharp.Android/bin/Release/SkiaSharp.dll", "./output/android/");
    CopyFileToDirectory ("./binding/SkiaSharp.iOS/bin/Release/SkiaSharp.dll", "./output/ios/");
    CopyFileToDirectory ("./binding/SkiaSharp.tvOS/bin/Release/SkiaSharp.dll", "./output/tvos/");
    CopyFileToDirectory ("./binding/SkiaSharp.OSX/bin/Release/SkiaSharp.dll", "./output/osx/");
    CopyFileToDirectory ("./binding/SkiaSharp.OSX/bin/Release/SkiaSharp.OSX.targets", "./output/osx/");
    CopyFileToDirectory ("./binding/SkiaSharp.Portable/bin/Release/SkiaSharp.dll", "./output/portable/");
    CopyFileToDirectory ("./binding/SkiaSharp.Desktop/bin/Release/SkiaSharp.dll", "./output/mac/");
    CopyFileToDirectory ("./binding/SkiaSharp.Desktop/bin/Release/SkiaSharp.Desktop.targets", "./output/mac/");
    CopyFileToDirectory ("./binding/SkiaSharp.Desktop/bin/Release/SkiaSharp.dll.config", "./output/mac/");
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// TESTS - some test cases to make sure it works
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("tests")
    .IsDependentOn ("libs")
    .Does (() =>
{
    RunNuGetRestore ("./tests/SkiaSharp.Desktop.Tests/SkiaSharp.Desktop.Tests.sln");

    // Windows (x86 and x64)
    if (IsRunningOnWindows ()) {
        DotNetBuild ("./tests/SkiaSharp.Desktop.Tests/SkiaSharp.Desktop.Tests.sln", c => {
            c.Configuration = "Release";
            c.Properties ["Platform"] = new [] { "x86" };
        });
        RunTests("./tests/SkiaSharp.Desktop.Tests/bin/x86/Release/SkiaSharp.Desktop.Tests.dll");
        DotNetBuild ("./tests/SkiaSharp.Desktop.Tests/SkiaSharp.Desktop.Tests.sln", c => {
            c.Configuration = "Release";
            c.Properties ["Platform"] = new [] { "x64" };
        });
        RunTests("./tests/SkiaSharp.Desktop.Tests/bin/x86/Release/SkiaSharp.Desktop.Tests.dll");
    }
    // Mac OSX (Any CPU)
    if (IsRunningOnUnix ()) {
        DotNetBuild ("./tests/SkiaSharp.Desktop.Tests/SkiaSharp.Desktop.Tests.sln", c => {
            c.Configuration = "Release";
        });
        RunTests("./tests/SkiaSharp.Desktop.Tests/bin/AnyCPU/Release/SkiaSharp.Desktop.Tests.dll");
    }
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// SAMPLES - the demo apps showing off the work
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("samples")
    .IsDependentOn ("libs")
    .Does (() =>
{
    // first we need to add our new nuget to the cache so we can restore
    // we first need to delete the old stuff
    DirectoryPath home = EnvironmentVariable ("USERPROFILE") ?? EnvironmentVariable ("HOME");
    var installedNuGet = home.Combine (".nuget").Combine ("packages").Combine ("SkiaSharp");
    if (DirectoryExists (installedNuGet)) {
        Warning ("SkiaSharp nugets were installed at '{0}', removing...", installedNuGet);
        CleanDirectory (installedNuGet);
    }

    if (IsRunningOnUnix ()) {
        RunNuGetRestore ("./samples/Skia.OSX.Demo/Skia.OSX.Demo.sln");
        DotNetBuild ("./samples/Skia.OSX.Demo/Skia.OSX.Demo.sln", c => {
            c.Configuration = "Release";
        });
        RunNuGetRestore ("./samples/Skia.Forms.Demo/Skia.Forms.Demo.Mac.sln");
        DotNetBuild ("./samples/Skia.Forms.Demo/Skia.Forms.Demo.Mac.sln", c => {
            c.Configuration = "Release";
            c.Properties ["Platform"] = new [] { "iPhone" };
        });
        RunNuGetRestore ("./samples/Skia.tvOS.Demo/Skia.tvOS.Demo.sln");
        DotNetBuild ("./samples/Skia.tvOS.Demo/Skia.tvOS.Demo.sln", c => {
            c.Configuration = "Release";
            c.Properties ["Platform"] = new [] { "iPhoneSimulator" };
        });
    }

    if (IsRunningOnWindows ()) {
        RunNuGetRestore ("./samples/Skia.UWP.Demo/Skia.UWP.Demo.sln");
        DotNetBuild ("./samples/Skia.UWP.Demo/Skia.UWP.Demo.sln", c => {
            c.Configuration = "Release";
        });
        RunNuGetRestore ("./samples/Skia.Forms.Demo/Skia.Forms.Demo.Windows.sln");
        DotNetBuild ("./samples/Skia.Forms.Demo/Skia.Forms.Demo.Windows.sln", c => {
            c.Configuration = "Release";
        });
    }

    RunNuGetRestore ("./samples/Skia.WindowsDesktop.Demo/Skia.WindowsDesktop.Demo.sln");
    DotNetBuild ("./samples/Skia.WindowsDesktop.Demo/Skia.WindowsDesktop.Demo.sln", c => {
        c.Configuration = "Release";
        c.Properties ["Platform"] = new [] { "x86" };
    });
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// DOCS - building the API documentation
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("docs")
    .IsDependentOn ("libs-base")
    .IsDependentOn ("externals-genapi")
    .Does (() =>
{
    RunMdocUpdate ("./binding/SkiaSharp.Generic/bin/Release/SkiaSharp.dll", "./docs/en/");

    if (!DirectoryExists ("./output/docs/msxml/")) CreateDirectory ("./output/docs/msxml/");
    RunMdocMSXml ("./docs/en/", "./output/docs/msxml/SkiaSharp.xml");

    if (!DirectoryExists ("./output/docs/mdoc/")) CreateDirectory ("./output/docs/mdoc/");
    RunMdocAssemble ("./docs/en/", "./output/docs/mdoc/SkiaSharp");
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// NUGET - building the package for NuGet.org
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("nuget")
    .IsDependentOn ("libs")
    .IsDependentOn ("docs")
    .Does (() =>
{
    // we can only build the combined package on CI
    if (TARGET == "CI") {
        PackageNuGet ("./nuget/SkiaSharp.nuspec", "./output/");
    } else {
        if (IsRunningOnWindows ()) {
            PackageNuGet ("./nuget/SkiaSharp.Windows.nuspec", "./output/");
        }
        if (IsRunningOnUnix ()) {
            PackageNuGet ("./nuget/SkiaSharp.Mac.nuspec", "./output/");
        }
    }
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// COMPONENT - building the package for components.xamarin.com
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("component")
    .IsDependentOn ("nuget")
    .Does (() =>
{
    // TODO: Not yet ready

    // if (!DirectoryExists ("./output/")) {
    //     CreateDirectory ("./output/");
    // }

    // FilePath yaml = "./component/component.yaml";
    // var yamlDir = yaml.GetDirectory ();
    // PackageComponent (yamlDir, new XamarinComponentSettings {
    //     ToolPath = XamarinComponentToolPath
    // });

    // MoveFiles (yamlDir.FullPath.TrimEnd ('/') + "/*.xam", "./output/");
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// CLEAN - remove all the build artefacts
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("clean")
    .IsDependentOn ("clean-externals")
    .IsDependentOn ("clean-managed")
    .Does (() =>
{
});
Task ("clean-managed").Does (() =>
{
    CleanDirectories ("./binding/*/bin");
    CleanDirectories ("./binding/*/obj");

    CleanDirectories ("./samples/*/bin");
    CleanDirectories ("./samples/*/obj");
    CleanDirectories ("./samples/*/*/bin");
    CleanDirectories ("./samples/*/*/obj");
    CleanDirectories ("./samples/*/packages");

    CleanDirectories ("./tests/**/bin");
    CleanDirectories ("./tests/**/obj");

    if (DirectoryExists ("./output"))
        DeleteDirectory ("./output", true);
});
Task ("clean-externals").Does (() =>
{
    // skia
    CleanDirectories ("skia/out");
    CleanDirectories ("skia/xcodebuild");

    // all
    CleanDirectories ("native-builds/lib");
    // android
    CleanDirectories ("native-builds/libSkiaSharp_android/obj");
    CleanDirectories ("native-builds/libSkiaSharp_android/libs");
    // ios
    CleanDirectories ("native-builds/libSkiaSharp_ios/build");
    // tvos
    CleanDirectories ("native-builds/libSkiaSharp_tvos/build");
    // osx
    CleanDirectories ("native-builds/libSkiaSharp_osx/build");
    // windows
    CleanDirectories ("native-builds/libSkiaSharp_windows/Release");
    CleanDirectories ("native-builds/libSkiaSharp_windows/x64/Release");

    // remove compatibility
    InjectCompatibilityExternals (false);
});

////////////////////////////////////////////////////////////////////////////////////////////////////
// DEFAULT - target for common development
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("Default")
    .IsDependentOn ("externals")
    .IsDependentOn ("libs");

Task ("Everything")
    .IsDependentOn ("externals")
    .IsDependentOn ("libs")
    .IsDependentOn ("docs")
    .IsDependentOn ("nuget")
    .IsDependentOn ("component")
    .IsDependentOn ("tests")
    .IsDependentOn ("samples");

////////////////////////////////////////////////////////////////////////////////////////////////////
// CI - the master target to build everything
////////////////////////////////////////////////////////////////////////////////////////////////////

Task ("CI")
    .IsDependentOn ("externals")
    .IsDependentOn ("libs")
    .IsDependentOn ("docs")
    .IsDependentOn ("nuget")
    .IsDependentOn ("component")
    .IsDependentOn ("tests")
    .IsDependentOn ("samples");

Task ("Windows-CI")
    .IsDependentOn ("externals")
    .IsDependentOn ("libs")
    .IsDependentOn ("docs")
    .IsDependentOn ("nuget")
    .IsDependentOn ("component")
    .IsDependentOn ("tests")
    .IsDependentOn ("samples");

////////////////////////////////////////////////////////////////////////////////////////////////////
// BUILD NOW
////////////////////////////////////////////////////////////////////////////////////////////////////

Information ("Cake.exe ToolPath: {0}", CakeToolPath);
Information ("Cake.exe NUnitConsoleToolPath: {0}", NUnitConsoleToolPath);
Information ("NuGet.exe ToolPath: {0}", NugetToolPath);
Information ("Xamarin-Component.exe ToolPath: {0}", XamarinComponentToolPath);
Information ("genapi.exe ToolPath: {0}", GenApiToolPath);

ListEnvironmentVariables ();

RunTarget (TARGET);
