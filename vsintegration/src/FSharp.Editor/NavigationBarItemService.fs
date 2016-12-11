// Copyright (c) Microsoft Corporation.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

namespace Microsoft.VisualStudio.FSharp.Editor

open System
open System.Composition
open System.Collections.Generic
open System.Collections.Immutable
open System.Linq
open System.Threading
open System.Threading.Tasks
open System.Runtime.CompilerServices

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Classification
open Microsoft.CodeAnalysis.Editor
open Microsoft.CodeAnalysis.Editor.Host
open Microsoft.CodeAnalysis.Navigation
open Microsoft.CodeAnalysis.Editor.Shared.Utilities
open Microsoft.CodeAnalysis.Host.Mef
open Microsoft.CodeAnalysis.Text

open Microsoft.VisualStudio.FSharp.LanguageService
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Tagging
open Microsoft.VisualStudio.Shell
open Microsoft.VisualStudio.TextManager.Interop

open Microsoft.FSharp.Compiler.Range
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.FSharp.Compiler.Ast

[<ExportLanguageService(typeof<INavigationBarItemService>, FSharpCommonConstants.FSharpLanguageName); Shared>]
type internal FSharpNavigationBarItemService
    [<ImportingConstructor>]
    (
        checkerProvider: FSharpCheckerProvider,
        projectInfoManager: ProjectInfoManager
        //[<System.ComponentModel.Composition.Import(typeof<SVsServiceProvider>)>] serviceProvider: IServiceProvider
    ) =
    
    static let emptyResult: IList<NavigationBarItem> = upcast [||]
    //let vsTextManager = serviceProvider.GetService(typeof<SVsTextManager>) :?> IVsTextManager

    interface INavigationBarItemService with
        member __.GetItemsAsync(document, cancellationToken) : Task<IList<NavigationBarItem>> = 
            async {
                match projectInfoManager.TryGetOptionsForEditingDocumentOrProject(document)  with 
                | Some options ->
                    let! sourceText = document.GetTextAsync(cancellationToken) |> Async.AwaitTask
                    let! fileParseResults = checkerProvider.Checker.ParseFileInProject(document.FilePath, sourceText.ToString(), options)
                    match fileParseResults.ParseTree with
                    | Some parsedInput ->
                        match parsedInput with
                        | ParsedInput.SigFile _ -> return emptyResult
                        | ParsedInput.ImplFile(ParsedImplFileInput(_, _, _, _, _, moduleOrNamespaceList, _)) -> 
                            let navItems = NavigationImpl.getNavigationFromImplFile(moduleOrNamespaceList)
                            let rangeToTextSpan range = 
                                try Some(CommonRoslynHelpers.FSharpRangeToTextSpan(sourceText, range))
                                with _ -> None
                            return 
                                navItems.Declarations
                                |> Array.choose (fun topLevelDecl ->
                                    rangeToTextSpan(topLevelDecl.Declaration.BodyRange)
                                    |> Option.map (fun topLevelTextSpan ->
                                        let childItems =
                                            topLevelDecl.Nested
                                            |> Array.choose (fun decl ->
                                                rangeToTextSpan(decl.Range)
                                                |> Option.map(fun textSpan ->
                                                    NavigationBarPresentedItem(
                                                        decl.Name, 
                                                        CommonHelpers.glyphMajorToRoslynGlyph(decl.GlyphMajor), 
                                                        [| textSpan |])
                                                    :> NavigationBarItem))
                                        
                                        NavigationBarPresentedItem(
                                            topLevelDecl.Declaration.Name, 
                                            CommonHelpers.glyphMajorToRoslynGlyph(topLevelDecl.Declaration.GlyphMajor),
                                            [| topLevelTextSpan |],
                                            childItems)
                                        :> NavigationBarItem)) :> IList<_>
                    | None -> return emptyResult
                | None -> return emptyResult
            } |> CommonRoslynHelpers.StartAsyncAsTask(cancellationToken)
        
        
        member __.ShowItemGrayedIfNear (_item) : bool = false
        
        member __.NavigateToItem(_document, item, view, _cancellationToken) =
            match item.Spans |> Seq.tryHead with
            | Some span ->
                view.Selection.Select(SnapshotSpan(view.TextBuffer.CurrentSnapshot, Span(span.Start, span.Length)), false)
            | None -> ()
