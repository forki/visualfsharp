// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.
// NOTE: this file is used by other parties integrating paket reference loading in scripting environments.
// Do not add any reference to F# codebase other than FSharp.Core.

// This file should end up in paket repository instead of F#.

/// Paket invokation for In-Script reference loading
module ReferenceLoading.PaketHandler

type ReferenceLoadingResult =
| Solved of loadingScript: string * additionalIncludeFolders : string list
| DependencyManagerNotFound of implicitIncludeDir: string * userProfile: string
| PackageResolutionFailed of toolPath: string * workingDir: string * msg : string

let MakeDependencyManagerCommand scriptType packageManagerTargetFramework projectRootDirArgument = 
    sprintf "install --generate-load-scripts load-script-type %s load-script-framework %s project-root \"%s\"" 
        scriptType packageManagerTargetFramework (System.IO.Path.GetFullPath projectRootDirArgument)

module Internals =
    open System
    open System.IO
    let PM_EXE = "paket.exe"
    let PM_DIR = ".paket"
    let PM_SPEC_FILE = "paket.dependencies"
    let PM_LOCK_FILE = "paket.lock"

    let userProfile =
        let res = Environment.GetEnvironmentVariable("USERPROFILE")
        if System.String.IsNullOrEmpty res then
            Environment.GetEnvironmentVariable("HOME")
        else res

    let getDiretoryAndAllParentDirectories (directory: DirectoryInfo) =
        let rec allParents (directory: DirectoryInfo) =
            seq {
                match directory.Parent with
                | null -> ()
                | parent -> 
                    yield parent
                    yield! allParents parent
            }

        seq {
            yield directory
            yield! allParents directory
        }

    /// Walks up directory structure and tries to find paket.exe
    let findPaketExe (prioritizedSearchPaths: DirectoryInfo seq) (baseDir: DirectoryInfo) =

        // for each given directory, we look for paket.exe and .paket/paket.exe
        let getPaketAndExe (directory: DirectoryInfo) =
            match directory.GetFiles(PM_EXE) with
            | [| exe |] -> Some exe.FullName
            | _ ->
                match directory.GetDirectories(PM_DIR) with
                | [| dir |] -> 
                    match dir.GetFiles(PM_EXE) with
                    | [| exe |] -> Some exe.FullName
                    | _ -> None
                | _ -> None

        let allDirs =
            Seq.concat [prioritizedSearchPaths ; getDiretoryAndAllParentDirectories baseDir]
        
        allDirs
        |> Seq.choose getPaketAndExe
        |> Seq.tryHead

    /// Resolve packages loaded into scripts using `paket:` in `#r` directives such as `#r @"paket: nuget AmazingNugetPackage"`. 
    /// <remarks>The result is either `ReferenceLoadingResult.Solved` or some of the failing cases.</remarks>
    /// <param name="targetFramework">A string given to paket command to fix the framework.</param>
    /// <param name="getCommand">Prepares the full `paket.exe` command, given the targetFramework and the callsite rootDir (to pass as project-root argument to paket.exe).</param>
    /// <param name="getRelativeLoadScriptLocation">Resolves the path (based from the passed working dir, which is temporary) to the load script.</param>
    /// <param name="alterToolPath">Function which prefixes the whole command, some platforms such as mono requires invocation of `mono ` as prefix to the full `paket.exe` command.</param>
    /// <param name="prioritizedSearchPaths">List of directories which are checked first to resolve `paket.exe`.</param>
    /// <param name="implicitIncludeDir">normally, the folder containing the script</param>
    /// <param name="scriptName">filename for the script (not necessarilly existing if interactive evaluation)</param>
    /// <param name="packageManagerTextLinesFromScript">package manager text lines from script, those are meant to be just the inner part, without `#r "paket:` prefix</param>
    let ResolvePackages 
        targetFramework
        getCommand
        getRelativeLoadScriptLocation
        alterToolPath
        prioritizedSearchPaths
        (implicitIncludeDir: string)
        (scriptName: string)
        (packageManagerTextLinesFromScript: string list)
        =
        let workingDir = Path.Combine(Path.GetTempPath(), "script-packages", string(abs(hash (implicitIncludeDir,scriptName))))
        let workingDirSpecFile = FileInfo(Path.Combine(workingDir,PM_SPEC_FILE))
        if not (Directory.Exists workingDir) then
            Directory.CreateDirectory workingDir |> ignore

        let packageManagerTextLinesFromScript = packageManagerTextLinesFromScript |> List.filter (not << String.IsNullOrWhiteSpace)
        
        let rootDir,packageManagerTextLines =
            let rec findSpecFile dir =
                let fi = FileInfo(Path.Combine(dir,PM_SPEC_FILE))
                if fi.Exists then
                    let lockFile = FileInfo(Path.Combine(fi.Directory.FullName,PM_LOCK_FILE))
                    let depsFileLines = File.ReadAllLines fi.FullName
                    if lockFile.Exists then
                        let originalDepsFile = FileInfo(workingDirSpecFile.FullName + ".original")
                        if not originalDepsFile.Exists ||
                           File.ReadAllLines originalDepsFile.FullName <> depsFileLines
                        then
                            File.Copy(fi.FullName,originalDepsFile.FullName,true)
                            let targetLockFile = FileInfo(Path.Combine(workingDir,PM_LOCK_FILE))
                            File.Copy(lockFile.FullName,targetLockFile.FullName,true)
                    
                    let lines = 
                        if List.isEmpty packageManagerTextLinesFromScript then 
                            Array.toList depsFileLines
                        else
                            (Array.toList depsFileLines) @ ("group Main" :: packageManagerTextLinesFromScript)

                    fi.Directory.FullName, lines
                elif not (isNull fi.Directory.Parent) then
                    findSpecFile fi.Directory.Parent.FullName
                else
                    workingDir, ("framework: " + targetFramework) :: "source https://nuget.org/api/v2" :: packageManagerTextLinesFromScript
           
            findSpecFile implicitIncludeDir

        let loadScript = getRelativeLoadScriptLocation workingDir
        let additionalIncludeFolders() = 
            [Path.Combine(workingDir,"paket-files")]
            |> List.filter Directory.Exists

        if workingDirSpecFile.Exists && 
            (File.ReadAllLines(workingDirSpecFile.FullName) |> Array.toList) = packageManagerTextLines && 
            File.Exists loadScript
        then 
            printfn "skipping running package resolution... already done that" 
            Solved(loadScript,additionalIncludeFolders())
        else 
            let toolPathOpt = 
                // we try to resolve .paket/paket.exe any place up in the folder structure from current script
                match findPaketExe prioritizedSearchPaths (DirectoryInfo implicitIncludeDir) with
                | Some paketExe -> Some paketExe
                | None ->
                    let profileExe = Path.Combine (userProfile, PM_DIR, PM_EXE)
                    if File.Exists profileExe then Some profileExe
                    else None

            match toolPathOpt with 
            | None -> 
                DependencyManagerNotFound(implicitIncludeDir, userProfile)

            | Some toolPath ->
                try File.Delete(loadScript) with _ -> ()
                let toolPath = alterToolPath toolPath
                File.WriteAllLines(workingDirSpecFile.FullName, packageManagerTextLines)
                printfn "running package resolution in '%s'..." workingDir
                let startInfo = 
                    System.Diagnostics.ProcessStartInfo(
                        FileName = toolPath,
                        WorkingDirectory = workingDir, 
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        Arguments = getCommand targetFramework rootDir,
                        CreateNoWindow = true,
                        UseShellExecute = false)
                
                use p = new System.Diagnostics.Process()
                let errors = System.Collections.Generic.List<_>()
                let log = System.Collections.Generic.List<_>()
                p.StartInfo <- startInfo
                p.ErrorDataReceived.Add(fun d -> if d.Data <> null then errors.Add d.Data)
                p.OutputDataReceived.Add(fun d -> if d.Data <> null then log.Add d.Data)
                p.Start() |> ignore
                p.BeginErrorReadLine()
                p.BeginOutputReadLine()
                p.WaitForExit()

                printfn "done running package resolution..."
                if p.ExitCode <> 0 then
                    let msg = String.Join(Environment.NewLine, errors)
                    PackageResolutionFailed(toolPath, workingDir, msg)
                else
                    printfn "package resolution completed at %A" System.DateTimeOffset.UtcNow
                    Solved(loadScript,additionalIncludeFolders())

/// Resolves absolute load script location: something like
/// baseDir/.paket/load/scriptName
/// or
/// baseDir/.paket/load/frameworkDir/scriptName 
let GetPaketLoadScriptLocation baseDir optionalFrameworkDir scriptName =
    let paketLoadFolder = System.IO.Path.Combine(Internals.PM_DIR,"load")
    let frameworkDir =
        match optionalFrameworkDir with 
        | None -> paketLoadFolder 
        | Some frameworkDir -> System.IO.Path.Combine(paketLoadFolder, frameworkDir)

    System.IO.Path.Combine(baseDir, frameworkDir, scriptName)
