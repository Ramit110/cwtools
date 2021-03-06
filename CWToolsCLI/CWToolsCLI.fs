namespace CWToolsCLI
open System.Text
open CWTools.Common
open System.IO
open CWTools.Parser
open CWTools
open FParsec
open System.Diagnostics.Tracing
open System.Reflection
open CWTools.Localisation
open CWTools.Localisation.STLLocalisation
open CWTools.Games.Files
open CWTools.Common.STLConstants
open CWTools.Games
open CWTools.Validation.Stellaris
open MBrace.FsPickler
open CWTools.Process
open CWTools.Utilities.Position
open System
open FSharpPlus.Control

module CWToolsCLI =
    open Argu
    open Validator

    type Exiter() =
        interface IExiter with
            member __.Name = "paket exiter"
            member __.Exit (msg,code) =
                if code = ErrorCode.HelpText then
                    printfn "%s" msg ; exit 0
                else eprintfn "%s" msg ; exit 1

    type ListTypes =
        | Folders = 1
        | Files = 2
        | Triggers = 3
        | Effects = 4
        | Localisation = 5
        | Technology = 6
        | Types = 7
    type ListSort =
        | Path = 1
    type ListArgs =
        | [<MainCommand; ExactlyOnce; Last>] ListType of ListTypes
        | Sort of ListSort option
    with
        interface IArgParserTemplate with
            member s.Usage =
                match s with
                | ListType _ -> "Thing to list"
                | Sort _ -> "Sort by"
    type ValidateType =
        | ParseErrors = 1
        | Errors = 2
        | Warnings = 3
        | Info = 4
        | All = 5
        | Localisation = 6
    type ValidateOutputFormat =
        | Detailed
        | Summary
    type ValidateArgs =
        | [<MainCommand; ExactlyOnce; Last>] ValType of ValidateType
        | OutputFormat of ValidateOutputFormat
        | OutputFile of filename:string
    with
        interface IArgParserTemplate with
            member s.Usage =
                match s with
                | ValType _ -> "Which errors to output"
                | OutputFormat _ -> "How to output validation errors"
                | OutputFile _ -> "Output errors to a csv with this filename"
    type ParseArgs =
        | [<MainCommand; ExactlyOnce; Last>] File of string
    with
        interface IArgParserTemplate with
            member s.Usage =
                match s with
                |File _ -> "file to parse"
    type SerializeArgs =
        | [<MainCommand; ExactlyOnce; Last>] Parse of bool
    with
        interface IArgParserTemplate with
            member s.Usage =
                match s with
                |Parse _ -> "hack"
    type Arguments =
        | [<Inherit>]Directory of path : string
        | [<Inherit>]Game of Game
        | [<Inherit>]Scope of FilesScope
        | [<Inherit>]ModFilter of string
        | [<Inherit>]CacheFile of path : string
        | DocsPath of string
        | [<CustomCommandLine("validate")>] Validate of ParseResults<ValidateArgs>
        | [<CustomCommandLine("list")>] List of ParseResults<ListArgs>
        | [<CustomCommandLine("parse")>] Parse of ParseResults<ParseArgs>
        | [<CliPrefix(CliPrefix.None)>] Serialize of ParseResults<SerializeArgs>

    with
        interface IArgParserTemplate with
            member s.Usage =
                match s with
                | Directory _ -> "specify the main game directory (default: current directory)"
                | Game _ -> "specify the game"
                | Validate _ -> "Validate all mod files (default: mods)"
                | List _ -> "List things"
                | Scope _ -> "which files to include"
                | ModFilter _ -> "filter to mods with this in name"
                | DocsPath _ -> "path to a custom trigger_docs game.log file"
                | Parse _ -> "parse a file"
                | Serialize _ -> "created serialized files for embedding"


    let getEffectsAndTriggers docsPath =
        let docsParsed =
            match docsPath with
            | Some path -> DocsParser.parseDocsFile path
            | None ->
                DocsParser.parseDocsStream (Assembly.GetEntryAssembly().GetManifestResourceStream("CWToolsCLI.game_effects_triggers_1.9.1.txt"))
        match docsParsed with
        |Success(p, _, _) -> p |> (DocsParser.processDocs parseScopes)
        |Failure(msg,_,_) -> failwith ("docs parsing failed with " + msg)
    let merge (a : Map<'a, 'b>) (b : Map<'a, 'b>) (f : 'a -> 'b * 'b -> 'b) =
        Map.fold (fun s k v ->
            match Map.tryFind k s with
            | Some v' -> Map.add k (f k (v, v')) s
            | None -> Map.add k v s) a b
    let rec getAllFolders dirs =
        if Seq.isEmpty dirs then Seq.empty else
            seq { yield! dirs |> Seq.collect Directory.EnumerateDirectories
                  yield! dirs |> Seq.collect Directory.EnumerateDirectories |> getAllFolders }
    let getAllFoldersUnion dirs =
        seq {
            yield! dirs
            yield! getAllFolders dirs
        }

    let getConfigFiles(gameDir : string option) =
        let configpath = "Main.files.config.cwt"
        let configDir =
            match gameDir with
            |Some dir -> Path.Combine(dir, ".cwtools")
            |None -> "./.cwtools"
        let configFiles = (if Directory.Exists configDir then getAllFoldersUnion ([configDir] |> Seq.ofList) else Seq.empty) |> Seq.collect (Directory.EnumerateFiles)
        let configFiles = configFiles |> List.ofSeq |> List.filter (fun f -> Path.GetExtension f = ".cwt")
        let configs =
            match true, configFiles.Length > 0 with
            |false, _ -> []
            |_, true ->
                configFiles |> List.map (fun f -> f, File.ReadAllText(f))
                //["./config.cwt", File.ReadAllText("./config.cwt")]
            |_, false -> []
        configs
    let list game directory scope modFilter docsPath (results : ParseResults<ListArgs>) =
        let triggers, effects = getEffectsAndTriggers docsPath
        let gameObj = STL(directory, scope, modFilter, triggers, effects, getConfigFiles(Some directory))
        let sortOrder = results.GetResult <@ Sort @>
        match results.GetResult <@ ListType @> with
        | ListTypes.Folders -> printfn "%A" gameObj.folders
        | ListTypes.Files ->
            match sortOrder with
            | None
            | Some ListSort.Path ->
                let files = gameObj.allFileList |> List.map (sprintf "%O")
                File.WriteAllLines("files.csv", files)
                //gameObj.allFileList |> List.iter (fun f -> printfn "%O" f)
            | _ -> failwith "Unexpected sort order"
        | ListTypes.Triggers ->
            // let triggers = DocsParser.parseDocs "C:\Users\Jennifer\Documents\Thomas\CK2Events\CK2EventsTests\game_triggers (1).txt"
            // let t = triggers |>  (function |Success(p, _, _) -> p |_ -> [])
            let t = gameObj.scriptedTriggerList
            printfn "%A" t
        | ListTypes.Effects ->
            let t = gameObj.scriptedEffectList
            printfn "%A" t
        | ListTypes.Localisation -> ()
            //printfn "%A" loc.GetKeys
        | ListTypes.Technology ->
            (gameObj.references().Technologies) |> List.map fst |> List.iter (printfn "%A")
        | ListTypes.Types ->
            gameObj.recompute()
            let referencedTypes = gameObj.entities() |> List.choose (fun struct(e,l) -> l.Force().Referencedtypes)
            // printfn "%A" referencedTypes
            let combinedReferences = referencedTypes |> List.fold (fun s m -> merge s m (fun _ (a,b) -> a @ b)) Map.empty
                                        |> Map.map (fun _ vs -> vs |> List.map fst)
            let types = gameObj.references().TypeMapInfo |> Map.map (fun _ vs -> vs |> List.map fst)
            let events = types |> Map.tryFind "scripted_trigger" |> Option.defaultValue []
            let eventReferences = combinedReferences |> Map.tryFind "scripted_trigger" |> Option.defaultValue []
            // eventReferences |> List.iter (printfn "%s")
            let unused = List.except eventReferences events
            unused |> List.iter (printfn "%s")
            let files = events |> List.map (sprintf "%A")
            File.WriteAllLines("file1.csv", files)
            let files = eventReferences |> List.map (sprintf "%A")
            File.WriteAllLines("file2.csv", files)


        | _ -> failwith "Unexpected list type"

    let validate game directory scope modFilter docsPath cachePath (results : ParseResults<_>) =
        let cached, cachedFiles =
            match cachePath with
            |Some path ->
                let doesCacheExist = File.Exists path
                printfn "%b" doesCacheExist
                if doesCacheExist then Serializer.deserialize path else [], []
            |None -> [], []
        //printfn "%A" cachedFiles
        let  triggers, effects = getEffectsAndTriggers docsPath
        let valType = results.GetResult <@ ValType @>
        let gameObj = ErrorGame(directory, scope, modFilter, triggers, effects, getConfigFiles(Some directory),game, cached, cachedFiles)
        let errors =
            match valType with
            | ValidateType.ParseErrors -> (gameObj.parserErrorList) |> List.map ValidationViewModelRow.Parse
            | ValidateType.Errors -> (gameObj.validationErrorList()) |> List.filter (fun e -> e.severity = Severity.Error) |> List.map Error
            | ValidateType.Warnings ->(gameObj.validationErrorList()) |> List.filter (fun e -> e.severity = Severity.Warning) |> List.map Error
            | ValidateType.Info ->(gameObj.validationErrorList()) |> List.filter (fun e -> e.severity = Severity.Information) |> List.map Error
            | ValidateType.Localisation ->
                gameObj.localisationErrorList() |> List.map Error
                //|> List.iter (fun l -> printfn "%O" l)
                //printfn "%A" (gameObj.localisationErrorList)
            | ValidateType.All ->
                ((gameObj.parserErrorList) |> List.map ValidationViewModelRow.Parse)
                @
                ((gameObj.validationErrorList()) |> List.map ValidationViewModelRow.Error)
                // printfn "%A" gameObj.parserErrorList;
                // pprintfn "%A" (gameObj.validationErrorList());
                //printfn "%A" (gameObj.parserErrorList.Length + (gameObj.validationErrorList().Length))
            | _ -> failwith "Unexpected validation type"
        let outputFormat = results.TryGetResult <@ OutputFormat @> |> Option.defaultValue Detailed
        let outputFile = results.TryGetResult <@ OutputFile @>
        let ne = Environment.NewLine
        let result =
            match outputFormat, outputFile with
            |Summary, _ -> sprintf "%i errors found" errors.Length
            |Detailed, None ->
                let sb = new StringBuilder()
                errors |> List.iter (fun e -> e |> function |ValidationViewModelRow.Parse(r) -> sb.Append(ne).Append(r.file).Append(ne).Append(r.error) |>ignore |Error(r) ->  sb.Append(ne).Append(r.position).Append(ne).Append(r.error) |> ignore)
                sb.ToString()
                // List.fold (fun s e -> e |> function |ValidationViewModelRow.Parse(r) -> s + ne + r.file + ne + r.error |Error(r) -> s + ne + r.position + ne + r.error) "" errors
            |Detailed, Some _ ->
                let sb = new StringBuilder()
                sb.Append("file,error") |> ignore
                errors
                    |> List.iter (fun e -> e |> function |ValidationViewModelRow.Parse(r) -> sb.Append(ne).Append(r.file).Append(",\"").Append(r.error.Replace(ne, "")).Append("\"") |> ignore |Error(r) ->  sb.Append(ne).Append(r.position).Append(",").Append(r.error.Replace(ne,"")) |> ignore)
                sb.ToString()
                // List.fold (fun s e -> e |> function |ValidationViewModelRow.Parse(r) -> s + ne + r.file + ",\"" + r.error.Replace(System.Environment.NewLine,"") + "\"" |Error(r) -> s + ne + r.position + "," + r.error.Replace(ne, "")) "file,error" errors
        match outputFile with
        |Some file ->
            File.WriteAllText(file, result)
        |None -> printf "%s" result
    let parse file =
        match CKParser.parseFile file with
        |Success(_,_,_) -> true, ""
        |Failure(msg,_,_) -> false, msg


    let serialize game directory scope modFilter docsPath =
        match game with
        |Game.STL ->
            Serializer.serializeSTL directory ""
        |Game.HOI4 ->
            Serializer.serializeHOI4 directory ""
        // let fileManager = FileManager(directory, Some modFilter, scope, scriptFolders, "stellaris", Encoding.UTF8)
        // let files = fileManager.AllFilesByPath()
        // let resources = ResourceManager(STLCompute.computeSTLData (fun () -> None)).Api
        // let entities = resources.UpdateFiles(files) |> List.map (fun (r, (struct (e, _))) -> r, e)
        // let files = resources.GetResources()
        //             |> List.choose (function |FileResource (_, r) -> Some (r.logicalpath, "")
        //                                         |FileWithContentResource (_, r) -> Some (r.logicalpath, r.filetext)
        //                                         |_ -> None)
        // let data = { resources = entities; fileIndexTable = fileIndexTable; files = files}
        // let pickle = xmlSerializer.Pickle data
        // File.WriteAllBytes("pickled.xml", pickle)


    [<EntryPoint>]
    let main argv =
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        CWTools.Utilities.Utils.loglevel <- CWTools.Utilities.Utils.LogLevel.Verbose
        let parser = ArgumentParser.Create<Arguments>(programName = "CWToolsCLI.exe", errorHandler = new Exiter(),
                                                    checkStructure = false)
        let results = parser.ParseCommandLine argv
        // let results = parser.ParseCommandLine argv
        let directory = results.TryGetResult <@ Directory @> |> Option.defaultValue System.Environment.CurrentDirectory
        let game = results.GetResult <@ Game @>
        let scope = results.TryGetResult <@ Scope @> |> Option.defaultValue FilesScope.Mods
        let modFilter = results.GetResult(<@ ModFilter @>, defaultValue = "")
        let docsPath = results.TryGetResult <@ DocsPath @>
        let cachePath = results.TryGetResult <@ CacheFile @>
        match results.GetSubCommand() with
        | List r -> list game directory scope modFilter docsPath r
        | Validate r -> validate game directory scope modFilter docsPath cachePath r
        | Directory _
        | Serialize _ -> serialize game directory scope modFilter docsPath
        | Game _ -> failwith "internal error: this code should never be reached"

        //printfn "%A" argv
        0 // return an integer exit code
