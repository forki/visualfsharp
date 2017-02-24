// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace rec Microsoft.VisualStudio.FSharp.Editor

open System
open System.Composition
open System.Collections.Immutable
open System.Threading
open System.Threading.Tasks

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.CodeFixes
open Microsoft.CodeAnalysis.CodeActions

[<ExportCodeFixProvider(FSharpCommonConstants.FSharpLanguageName, Name = "ReplaceWithSuggestion"); Shared>]
type internal FSharpReplaceWithSuggestionCodeFixProvider() =
    inherit CodeFixProvider()
    let fixableDiagnosticIds = ["FS0039"; "FS1129"; "FS0495"] |> Set.ofList
    let maybeString = FSComp.SR.undefinedNameSuggestionsIntro()
        
    let createCodeFix (title: string, context: CodeFixContext, textChange: TextChange) =
        CodeAction.Create(
            title,
            (fun (cancellationToken: CancellationToken) ->
                async {
                    let! sourceText = context.Document.GetTextAsync()
                    return context.Document.WithText(sourceText.WithChanges(textChange))
                } |> CommonRoslynHelpers.StartAsyncAsTask(cancellationToken)),
            title)

    override __.FixableDiagnosticIds = fixableDiagnosticIds.ToImmutableArray()

    override __.RegisterCodeFixesAsync context : Task =
        async { 
            context.Diagnostics 
            |> Seq.filter (fun x -> fixableDiagnosticIds |> Set.contains x.Id)
            |> Seq.iter (fun diagnostic ->
                let message = diagnostic.GetMessage()
                let parts = message.Split([| maybeString |], StringSplitOptions.None)
                if parts.Length > 1 then
                    let suggestions = 
                        parts.[1].Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) 
                        |> Array.map (fun s -> s.Trim())
                    
                    let diagnostics = [| diagnostic |].ToImmutableArray()

                    for suggestion in suggestions do
                        let replacement = if suggestion.Contains " " then "``" + suggestion + "``" else suggestion
                        let codefix = 
                            createCodeFix(
                                FSComp.SR.replaceWithSuggestion suggestion, 
                                context,
                                TextChange(context.Span, replacement))
                        context.RegisterCodeFix(codefix, diagnostics))
        } |> CommonRoslynHelpers.StartAsyncUnitAsTask(context.CancellationToken)
