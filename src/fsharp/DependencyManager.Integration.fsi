// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

/// Helper members to integrate DependencyManagers into F# codebase
module internal Microsoft.FSharp.Compiler.DependencyManagerIntegration

open Microsoft.FSharp.Compiler.Range

type IDependencyManagerProvider =
    inherit System.IDisposable
    abstract Name : string
    abstract ToolName: string
    abstract Key: string

val RegisteredDependencyManagers : unit -> Map<string,IDependencyManagerProvider>
val tryFindDependencyManagerInPath : string -> IDependencyManagerProvider option
val tryFindDependencyManagerByKey : string -> IDependencyManagerProvider option

val removeDependencyManagerKey : string -> string -> string

val resolve : IDependencyManagerProvider -> string -> string  -> range -> string list -> (string list * string * string) option
