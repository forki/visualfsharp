namespace Microsoft.FSharp.Compiler.SourceCodeServices

#if !NETSTANDARD1_6
open System.Runtime.Serialization.Json
open System.Runtime
open System.Diagnostics
#endif
open System.Text
open System.IO
open System
open System.Xml

type ProjectCracker =
    static member GetProjectOptionsFromProjectFileLogged(projectFileName : string, ?properties : (string * string) list, ?loadedTimeStamp, ?enableLogging) =
        let loadedTimeStamp = defaultArg loadedTimeStamp DateTime.MaxValue // Not 'now', we don't want to force reloading
        let properties = defaultArg properties []
        let enableLogging = defaultArg enableLogging true
        let logMap = ref Map.empty
        let cache = System.Collections.Generic.Dictionary<_,_>()

        let rec convert (opts: ProjectCrackerTool.ProjectOptions) : FSharpProjectOptions =
            match cache.TryGetValue opts with
            | true, r -> r
            | _ ->
                if not (isNull opts.Error) then failwith opts.Error

                let referencedProjects = Array.map (fun (a, b) -> a, convert b) opts.ReferencedProjectOptions
            
                let sourceFiles, otherOptions = 
                    opts.Options 
                    |> Array.partition (fun x -> 
                        let extension = Path.GetExtension(x).ToLower()
                        x.IndexOfAny(Path.GetInvalidPathChars()) = -1 
                        && (extension = ".fs" || extension = ".fsi"))
            
                let sepChar = Path.DirectorySeparatorChar
            
                let sourceFiles = sourceFiles |> Array.map (fun x -> 
                    match sepChar with
                    | '\\' -> x.Replace('/', '\\')
                    | '/' -> x.Replace('\\', '/')
                    | _ -> x
                )

                logMap := Map.add opts.ProjectFile opts.LogOutput !logMap
                let r =
                    { ProjectFileName = opts.ProjectFile
                      SourceFiles = sourceFiles
                      OtherOptions = otherOptions
                      ReferencedProjects = referencedProjects
                      IsIncompleteTypeCheckEnvironment = false
                      UseScriptResolutionRules = false
                      LoadTime = loadedTimeStamp
                      UnresolvedReferences = None 
                      OriginalLoadReferences = []
                      ExtraProjectInfo = None
                      Stamp = None }
                cache.Add(opts,r)
                r

#if NETSTANDARD1_6
        let arguments = [|
            yield projectFileName
            yield enableLogging.ToString()
            for k, v in properties do
                yield k
                yield v
        |]
        
        let ret, opts = Microsoft.FSharp.Compiler.SourceCodeServices.ProjectCrackerTool.ProjectCrackerTool.crackOpen arguments
        ignore ret
#else
        let arguments = new StringBuilder()
        arguments.Append('"').Append(projectFileName).Append('"') |> ignore
        arguments.Append(' ').Append(enableLogging.ToString()) |> ignore
        for k, v in properties do
            arguments.Append(' ').Append(k).Append(' ').Append(v) |> ignore
        let codebase = Path.GetDirectoryName(Uri(typeof<ProjectCracker>.Assembly.CodeBase).LocalPath)
        
        let crackerFilename = Path.Combine(codebase,"FSharp.Compiler.Service.ProjectCrackerTool.exe")
        if not (File.Exists crackerFilename) then failwithf "ProjectCracker exe not found at: %s it must be next to the ProjectCracker dll." crackerFilename

        let p = new System.Diagnostics.Process()

        p.StartInfo.FileName <- crackerFilename
        p.StartInfo.Arguments <- arguments.ToString()
        p.StartInfo.UseShellExecute <- false
        p.StartInfo.CreateNoWindow <- true
        p.StartInfo.RedirectStandardOutput <- true
        p.StartInfo.RedirectStandardError <- true

        let sbOut = StringBuilder()
        let sbErr = StringBuilder()
        
        p.ErrorDataReceived.AddHandler(fun _ a -> sbErr.AppendLine a.Data |> ignore)
        p.OutputDataReceived.AddHandler(fun _ a -> sbOut.AppendLine a.Data |> ignore)

        ignore <| p.Start()
    
        p.EnableRaisingEvents <- true
        p.BeginOutputReadLine()
        p.BeginErrorReadLine()

        p.WaitForExit()
        
        let crackerOut = sbOut.ToString()
        let crackerErr = sbErr.ToString()

        if p.ExitCode <> 0 then
            failwithf "'FSharp.Compiler.Service.ProjectCrackerTool.exe' exited with code %i.\nstdoutput was:\n%s\n\nstderr was:\n%s" p.ExitCode crackerOut crackerErr
            
        let opts = 
            try
                let ser = new DataContractJsonSerializer(typeof<ProjectCrackerTool.ProjectOptions>)
                let stringBytes = Encoding.Unicode.GetBytes crackerOut
                use ms = new MemoryStream(stringBytes)
                ser.ReadObject(ms) :?> ProjectCrackerTool.ProjectOptions
            with
              exn ->
                raise (Exception(sprintf "error parsing ProjectCrackerTool output, stdoutput was:\n%s\n\nstderr was:\n%s" crackerOut crackerErr, exn))
#endif
        
        convert opts, !logMap

    static member GetProjectOptionsFromProjectFile(projectFileName : string, ?properties : (string * string) list, ?loadedTimeStamp) =
        fst (ProjectCracker.GetProjectOptionsFromProjectFileLogged(
                projectFileName,
                ?properties=properties,
                ?loadedTimeStamp=loadedTimeStamp,
                enableLogging=false))
