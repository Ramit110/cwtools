namespace CWTools.Rules


open FParsec
open CWTools.Utilities.Position
open CWTools.Utilities
open CWTools.Parser.Types
open CWTools.Common
open CWTools.Common.STLConstants
open CWTools.Process.STLProcess
open CWTools.Process
open CWTools.Utilities.Utils
open System
open CWTools.Parser


type ReplaceScopes<'a> = {
    root : 'a option
    this : 'a option
    froms : 'a list option
    prevs : 'a list option
}
type Options<'a> = {
    min : int
    max : int
    leafvalue : bool
    description : string option
    pushScope : 'a option
    replaceScopes : ReplaceScopes<'a> option
    severity : Severity option
    requiredScopes : 'a list
    comparison : bool
}

[<Struct>]
type ValueType =
| Scalar
| Enum of enumc : string
| Specific of valuec : StringTokens
| Float of minmax: (float*float)
| Bool
| Int of minmaxi: (int*int)
| Percent
| Date
| CK2DNA
| CK2DNAProperty
| IRFamilyName
    override x.ToString() =
        match x with
        | Scalar -> "Scalar"
        | Enum enumc -> sprintf "Enum %s" enumc
        | Specific valuec -> sprintf "Specific %s" (StringResource.stringManager.GetStringForIDs valuec)
        | Float (min, max) -> sprintf "Float with min %f and max %f" min max
        | Bool -> "Bool"
        | Int (min, max) -> sprintf "Int with min %i and max %i" min max
        | Percent -> "Percent"
        | Date -> "Date"
        | CK2DNA -> "CK2DNA"
        | CK2DNAProperty -> "CK2DNAProperty"
        | IRFamilyName -> "IRFamilyName"

type TypeType =
| Simple of string
| Complex of prefix : string * name : string * suffix : string

type Marker =
| ColourField
| IRCountryTag

type TypeLocalisation<'a> = {
    name : string
    prefix : string
    suffix: string
    required : bool
    optional : bool
    explicitField : string option
    replaceScopes : ReplaceScopes<'a> option
}

type SkipRootKey = |SpecificKey of string |AnyKey |MultipleKeys of string list * bool
type SubTypeDefinition<'a> = {
    name : string
    rules : NewRule<'a> list
    typeKeyField : string option
    startsWith : string option
    pushScope : 'a option
    localisation : TypeLocalisation<'a> list
}
and TypeDefinition<'a> = {
    name : string
    nameField : string option
    path : string list
    path_strict : bool
    path_file : string option
    conditions : Node option
    subtypes : SubTypeDefinition<'a> list
    typeKeyFilter : (string list * bool) option
    skipRootKey : SkipRootKey list
    startsWith : string option
    type_per_file : bool
    warningOnly : bool
    unique : bool
    localisation : TypeLocalisation<'a> list
}

and NewField<'a> =
| ValueField of ValueType
| TypeField of TypeType
/// This is only used internally to match type definitions
| TypeMarkerField of dummyKey : StringLowerToken * typedef : TypeDefinition<'a>
| ScopeField of 'a
| LocalisationField of synced : bool
| FilepathField of prefix : string option * extension : string option
| IconField of string
| AliasField of string
| SingleAliasField of string
| SubtypeField of string * bool * NewRule<'a> list
| VariableSetField of string
| VariableGetField of string
| VariableField of isInt : bool * minmax : (float * float)
| ValueScopeMarkerField of isInt : bool * minmax : (float * float)
| ValueScopeField of isInt : bool * minmax : (float * float)
| MarkerField of Marker
    override x.ToString() =
        match x with
        | ValueField vt -> sprintf "Field of %O" vt
        | _ -> sprintf "Field of %A" x
and RuleType<'a> =
|NodeRule of left : NewField<'a> * rules : NewRule<'a> list
|LeafRule of left : NewField<'a> * right : NewField<'a>
|LeafValueRule of right : NewField<'a>
|ValueClauseRule of rules : NewRule<'a> list
|SubtypeRule of string * bool * NewRule<'a> list
    override x.ToString() =
        match x with
        | NodeRule (l, r) -> sprintf "NodeRule with Left (%O) and inner (%O)" l r
        | LeafRule (l, r) -> sprintf "LeafRule with Left (%O) and right (%O)" l r
        | LeafValueRule (r) -> sprintf "LeafValueRule (%O)" r
        | ValueClauseRule (rs) -> sprintf "ValueClauseRule with inner (%O)" rs
        | SubtypeRule (n, p, r) -> sprintf "SubtypeRule %s with inner (%O)" n r
and NewRule<'a> = RuleType<'a> * Options<'a>

type RootRule<'a> =
| AliasRule of string * NewRule<'a>
| SingleAliasRule of string * NewRule<'a>
| TypeRule of string * NewRule<'a>
    override x.ToString() =
        match x with
        | AliasRule (n, r) -> sprintf "Alias definition %s (%O)" n r
        | SingleAliasRule (n, r) -> sprintf "Single alias definition %s (%O)" n r
        | TypeRule (n, r) -> sprintf "Type rule %s (%O)" n r
// type EffectRule = Rule // Add scopes

type EnumDefinition = {
        key : string
        description : string
        values : string list
    }
type ComplexEnumDef = {
    name : string
    description : string
    path : string
    nameTree : Node
    start_from_root : bool
}

[<RequireQualifiedAccess>]
module RulesParser =
    let specificField x = ValueField(ValueType.Specific (StringResource.stringManager.InternIdentifierToken x))
    let private parseSeverity =
        function
        |"error" -> Severity.Error
        |"warning" -> Severity.Warning
        |"info" -> Severity.Information
        |"information" -> Severity.Information
        |"hint" -> Severity.Hint
        |s -> failwithf "Invalid severity %s" s
    let defaultOptions = { min = 0; max = 1000; leafvalue = false; description = None; pushScope = None; replaceScopes = None; severity = None; requiredScopes = []; comparison = false }
    let requiredSingle = { defaultOptions with min = 1; max = 1 }
    let requiredMany<'a> = { defaultOptions with min = 1; max = 100 }
    let optionalSingle = { defaultOptions with min = 0; max = 1 }
    let optionalMany = { defaultOptions with min = 0; max = 100 }

    let defaultFloat = ValueField (ValueType.Float (-1E+12, 1E+12))
    let defaultInt = ValueField (ValueType.Int (Int32.MinValue, Int32.MaxValue))

    let private getNodeComments (clause : IClause) =
        let findComments t s (a : Child) =
                match (s, a) with
                | ((b, c), _) when b -> (b, c)
                | ((_, c), CommentC nc) when nc.StartsWith("#") -> (false, nc::c)
                | ((_, c), CommentC nc) -> (false, c)
                | ((_, c), NodeC n) when n.Position = t -> (true, c)
                | ((_, c), LeafC v) when v.Position = t -> (true, c)
                | ((_, c), LeafValueC v) when v.Position = t -> (true, c)
                | ((_, c), ValueClauseC vc) when vc.Position = t -> (true, c)
                | _ -> (false, [])
                // | ((_, c), LeafValueC lv) when lv.Position = t -> (true, c)
                // | ((_, _), _) -> (false, [])
        //let fNode = (fun (node:Node) (children) ->
        let one = clause.Leaves |> Seq.map (fun e -> LeafC e, clause.AllArray |> Array.fold (findComments e.Position) (false, []) |> snd) |> List.ofSeq
        //log "%s %A" node.Key (node.All |> List.rev)
        //log "%A" one
        let two = clause.Nodes |> Seq.map (fun e -> NodeC e, clause.AllArray |> Array.fold (findComments e.Position) (false, []) |> snd |> (fun l -> (l))) |> List.ofSeq
        let three = clause.LeafValues |> Seq.toList |> List.map (fun e -> LeafValueC e, clause.AllArray |> Array.fold (findComments e.Position) (false, []) |> snd)
        let four = clause.ValueClauses |> Seq.toList |> List.map (fun e -> ValueClauseC e, clause.AllArray |> Array.fold (findComments e.Position) (false, []) |> snd)
        let new2 = one @ two @ three @ four
        new2

    let getSettingFromString (full : string) (key : string) =
        let setting = full.Substring(key.Length)
        if not (setting.StartsWith "[" && setting.EndsWith "]") then None else
            Some (setting.Substring(1, setting.Length - 2))

    let getFloatSettingFromString (full : string) =
        match getSettingFromString full "float" with
        |Some s ->
            let split = s.Split([|".."|], 2, StringSplitOptions.None)
            if split.Length < 2 then None else
                try
                    Some ((float split.[0]), (float split.[1]))
                with
                |_ -> None
        |None -> None


    let getIntSettingFromString (full : string) =
        match getSettingFromString full "int" with
        |Some s ->
            let split = s.Split([|".."|], 2, StringSplitOptions.None)
            if split.Length < 2 then None else
                try
                    Some ((int split.[0]), (int split.[1]))
                with
                |_ -> None
        |None -> None

    let getAliasSettingsFromString (full : string) =
        match getSettingFromString full "alias" with
        |Some s ->
            let split = s.Split([|":"|], 2, StringSplitOptions.None)
            if split.Length < 2 then None else Some (split.[0], split.[1])
        |None -> None
    let getSingleAliasSettingsFromString (full : string) =
        match getSettingFromString full "single_alias" with
        |Some s ->
            let split = s.Split([|":"|], 2, StringSplitOptions.None)
            if split.Length < 2 then None else Some (split.[0], split.[1])
        |None -> None


    let inline private replaceScopes parseScope (comments : string list) =
        match comments |> List.tryFind (fun s -> s.Contains("replace_scope")) with
        | Some s ->
            let s = s.Trim('#')
            let parsed = CKParser.parseString s "config"
            match parsed with
            | Failure(_) -> None
            | Success(s,_,_) ->
                let n = (STLProcess.shipProcess.ProcessNode EntityType.Other "root" (mkZeroFile "config") s)
                match n.Child "replace_scope" with
                | Some c ->
                    let this = if c.Has "this" then c.TagText "this" |> parseScope |> Some else None
                    let root = if c.Has "root" then c.TagText "root" |> parseScope |> Some else None
                    let from = if c.Has "from" then c.TagText "from" |> parseScope |> Some else None
                    let fromfrom = if c.Has "fromfrom" then c.TagText "fromfrom" |> parseScope |> Some else None
                    let fromfromfrom = if c.Has "fromfromfrom" then c.TagText "fromfromfrom" |> parseScope |> Some else None
                    let fromfromfromfrom = if c.Has "fromfromfromfrom" then c.TagText "fromfromfromfrom" |> parseScope |> Some else None
                    let froms = [from;fromfrom;fromfromfrom;fromfromfromfrom] |> List.choose id
                    let prev = if c.Has "prev" then c.TagText "prev" |> parseScope |> Some else None
                    let prevprev = if c.Has "prevprev" then c.TagText "prevprev" |> parseScope |> Some else None
                    let prevprevprev = if c.Has "prevprevprev" then c.TagText "prevprevprev" |> parseScope |> Some else None
                    let prevprevprevprev = if c.Has "prevprevprevprev" then c.TagText "prevprevprevprev" |> parseScope |> Some else None
                    let prevs = [prev;prevprev;prevprevprev;prevprevprevprev] |> List.choose id
                    Some { root = root; this = this; froms = Some froms; prevs = Some prevs }
                | None -> None
        | None -> None


    let getOptionsFromComments (parseScope) (allScopes) (anyScope) (operator : Operator) (comments : string list) =
        let min, max =
            match comments |> List.tryFind (fun s -> s.Contains("cardinality")) with
            | Some c ->
                let nums = c.Substring(c.IndexOf "=" + 1).Trim().Split([|".."|], 2, StringSplitOptions.None)
                try
                    match nums.[0], nums.[1] with
                    | min, "inf" -> (int min), 10000
                    | min, max -> (int min), (int max)
                with
                | _ -> 1, 1
            | None -> 1, 1
        let description =
            match comments |> List.tryFind (fun s -> s.StartsWith "##") with
            | Some d -> Some (d.Trim('#'))
            | None -> None
        let pushScope =
            match comments |> List.tryFind (fun s -> s.Contains("push_scope")) with
            | Some s -> s.Substring(s.IndexOf "=" + 1).Trim() |> parseScope |> Some
            | None -> None
        let reqScope =
            match comments |> List.tryFind (fun s -> s.StartsWith("# scope =")) with
            | Some s ->
                let rhs = s.Substring(s.IndexOf "=" + 1).Trim()
                match rhs.StartsWith("{") && rhs.EndsWith("}") with
                | true -> rhs.Trim('{','}') |> (fun s -> s.Split([|' '|])) |> Array.map parseScope |> List.ofArray
                | false -> let scope = rhs |> parseScope in if scope = anyScope then allScopes else [scope]
            | None -> []
        let severity =
            match comments |> List.tryFind (fun s -> s.Contains("severity")) with
            | Some s -> s.Substring(s.IndexOf "=" + 1).Trim() |> parseSeverity |> Some
            | None -> None
        let comparison = operator = Operator.EqualEqual
        { min = min; max = max; leafvalue = false; description = description; pushScope = pushScope; replaceScopes = replaceScopes parseScope comments; severity = severity; requiredScopes = reqScope; comparison = comparison }

    let processKey parseScope anyScope =
        function
        | "scalar" -> ValueField ValueType.Scalar
        | "bool" -> ValueField ValueType.Bool
        | "percentage_field" -> ValueField ValueType.Percent
        | "localisation" -> LocalisationField false
        | "localisation_synced" -> LocalisationField true
        | "filepath" -> FilepathField (None, None)
        | x when x.StartsWith "filepath[" ->
            match getSettingFromString x "filepath" with
            | Some (setting) ->
                match setting.Contains "," with
                | true ->
                    let [|folder; extension|] = setting.Split([|','|], 2)
                    FilepathField (Some folder, Some extension)
                | false ->
                    FilepathField (Some setting, None)
            | None -> FilepathField (None, None)
        | "date_field" -> ValueField Date
        | x when x.StartsWith "<" && x.EndsWith ">" ->
            TypeField (TypeType.Simple (x.Trim([|'<'; '>'|])))
        | x when x.Contains "<" && x.Contains ">" ->
            let x = x.Trim('"')
            let prefixI = x.IndexOf "<"
            let suffixI = x.IndexOf ">"
            TypeField (TypeType.Complex (x.Substring(0,prefixI), x.Substring(prefixI + 1, suffixI - prefixI - 1), x.Substring(suffixI + 1)))
        | "int" -> defaultInt
        | x when x.StartsWith "int[" ->
            match getIntSettingFromString x with
            | Some (min, max) -> ValueField (ValueType.Int (min, max))
            | None -> (defaultInt)
        | "float" -> defaultFloat
        | x when x.StartsWith "float" ->
            match getFloatSettingFromString x with
            | Some (min, max) -> ValueField (ValueType.Float (min, max))
            | None -> (defaultFloat)
        | x when x.StartsWith "enum[" ->
            match getSettingFromString x "enum" with
            | Some (name) -> ValueField (ValueType.Enum name)
            | None -> ValueField (ValueType.Enum "")
        | x when x.StartsWith "icon[" ->
            match getSettingFromString x "icon" with
            | Some (folder) -> IconField folder
            | None -> ValueField (ValueType.Scalar)
        | x when x.StartsWith "alias_match_left[" ->
            match getSettingFromString x "alias_match_left" with
            | Some alias -> AliasField alias
            | None -> ValueField ValueType.Scalar
        | x when x.StartsWith "alias_name[" ->
            match getSettingFromString x "alias_name" with
            | Some alias -> AliasField alias
            | None -> ValueField ValueType.Scalar
        | "scope_field" -> ScopeField (anyScope)
        | "variable_field" -> VariableField (false, (-1E+12, 1E+12))
        | x when x.StartsWith "variable_field[" ->
            match getFloatSettingFromString x with
            | Some (min, max) -> VariableField (false,(min, max))
            | None -> VariableField (false,(-1E+12, 1E+12))
        | "int_variable_field" -> VariableField (true, (float Int32.MinValue, float Int32.MaxValue))
        | x when x.StartsWith "int_variable_field[" ->
            match getIntSettingFromString x with
            | Some (min, max) -> VariableField (true,(float min,float max))
            | None -> VariableField (true,(float Int32.MinValue, float Int32.MaxValue))
        | "value_field" -> ValueScopeMarkerField (false, (-1E+12, 1E+12))
        | x when x.StartsWith "value_field[" ->
            match getFloatSettingFromString x with
            | Some (min, max) -> ValueScopeMarkerField (false,(min, max))
            | None -> ValueScopeMarkerField (false,(-1E+12, 1E+12))
        | "int_value_field" -> ValueScopeMarkerField (true, (float Int32.MinValue, float Int32.MaxValue))
        | x when x.StartsWith "int_value_field[" ->
            match getIntSettingFromString x with
            | Some (min, max) -> ValueScopeMarkerField (true,(float min,float max))
            | None -> ValueScopeMarkerField (true,(float Int32.MinValue, float Int32.MaxValue))
        | x when x.StartsWith "value_set[" ->
            match getSettingFromString x "value_set" with
            | Some variable ->
                VariableSetField variable
            | None -> ValueField ValueType.Scalar
        | x when x.StartsWith "value[" ->
            match getSettingFromString x "value" with
            | Some variable ->
                VariableGetField variable
            | None -> ValueField ValueType.Scalar
        | x when x.StartsWith "scope[" ->
            match getSettingFromString x "scope" with
            | Some target ->
                ScopeField (parseScope target)
            | None -> ValueField ValueType.Scalar
        | x when x.StartsWith "event_target" ->
            match getSettingFromString x "event_target" with
            | Some target ->
                ScopeField (parseScope target)
            | None -> ValueField ValueType.Scalar
        | x when x.StartsWith "single_alias_right" ->
            match getSettingFromString x "single_alias_right" with
            | Some alias ->
                SingleAliasField alias
            | None -> ValueField ValueType.Scalar
        | "portrait_dna_field" -> ValueField CK2DNA
        | "portrait_properties_field" -> ValueField CK2DNAProperty
        | "colour_field" -> MarkerField Marker.ColourField
        | "ir_country_tag_field" -> MarkerField Marker.IRCountryTag
        | "ir_family_name_field" -> ValueField IRFamilyName
        | x ->
            // eprintfn "ps %s" x
            ValueField (ValueType.Specific (StringResource.stringManager.InternIdentifierToken(x.Trim([|'\"'|]))))



    let configNode (processChildConfig) (parseScope) (allScopes) (anyScope) (node : Node) (comments : string list) (key : string) =
        let children = getNodeComments node
        let options = getOptionsFromComments parseScope allScopes anyScope (Operator.Equals) comments
        let innerRules = children |> List.choose (processChildConfig parseScope allScopes anyScope)
        let rule =
            match key with
            |x when x.StartsWith "subtype[" ->
                match getSettingFromString x "subtype" with
                |Some st when st.StartsWith "!" -> SubtypeRule (st.Substring(1), false, (innerRules))
                |Some st -> SubtypeRule (st, true, (innerRules))
                |None -> failwith (sprintf "Invalid subtype string %s" x)
            |x -> NodeRule(processKey parseScope anyScope x, innerRules)
            // |"int" -> NodeRule(ValueField(ValueType.Int(Int32.MinValue, Int32.MaxValue)), innerRules)
            // |"float" -> NodeRule(ValueField(ValueType.Float(Double.MinValue, Double.MaxValue)), innerRules)
            // |"scalar" -> NodeRule(ValueField(ValueType.Scalar), innerRules)
            // |"filepath" -> NodeRule(FilepathField, innerRules)
            // |"scope" -> NodeRule(ScopeField(Scope.Any), innerRules)
            // |x when x.StartsWith "enum[" ->
            //     match getSettingFromString x "enum" with
            //     |Some e -> NodeRule(ValueField(ValueType.Enum e), innerRules)
            //     |None -> failwith (sprintf "Invalid enum string %s" x)
            // |x when x.StartsWith "<" && x.EndsWith ">" ->
            //     NodeRule(TypeField(x.Trim([|'<'; '>'|])), innerRules)
            // |x -> NodeRule(ValueField(ValueType.Specific x), innerRules)
        NewRule(rule, options)

    let configValueClause processChildConfig (parseScope) (allScopes) (anyScope) (valueclause : ValueClause) (comments : string list) =
        let children = getNodeComments valueclause
        let options = getOptionsFromComments parseScope allScopes anyScope (Operator.Equals) comments
        let innerRules = children |> List.choose (processChildConfig parseScope allScopes anyScope)
        let rule = ValueClauseRule innerRules
        NewRule(rule, options)




    let rgbRule = LeafValueRule (ValueField (ValueType.Int (0, 255))), { min = 3; max = 4; leafvalue = true; description = None; pushScope = None; replaceScopes = None; severity = None; requiredScopes = []; comparison = false }
    let hsvRule = LeafValueRule (ValueField (ValueType.Float (0.0, 2.0))), { min = 3; max = 4; leafvalue = true; description = None; pushScope = None; replaceScopes = None; severity = None; requiredScopes = []; comparison = false }

    let configLeaf processChildConfig (parseScope) (allScopes) (anyScope) (leaf : Leaf) (comments : string list) (key : string) =
        let leftfield = processKey parseScope anyScope key
        let options = getOptionsFromComments parseScope allScopes anyScope (leaf.Operator) comments
        let rightkey = leaf.Value.ToString()
        match rightkey with
        |x when x.StartsWith("colour[") ->
            let colourRules =
                match getSettingFromString x "colour" with
                |Some "rgb" -> [rgbRule]
                |Some "hsv" -> [hsvRule]
                |_ -> [rgbRule; hsvRule]
            NewRule(NodeRule(leftfield, colourRules), options)
        |x ->
            let rightfield = processKey parseScope anyScope rightkey
            let leafRule = LeafRule(leftfield, rightfield)
            NewRule(leafRule, options)

    let configLeafValue processChildConfig (parseScope) allScopes (anyScope) (leafvalue : LeafValue) (comments : string list) =
        let field = processKey parseScope anyScope (leafvalue.Value.ToRawString())
            // match leafvalue.Value.ToRawString() with
            // |x when x.StartsWith "<" && x.EndsWith ">" ->
            //     TypeField (x.Trim([|'<'; '>'|]))
            // |x -> ValueField (ValueType.Enum x)
        let options = { getOptionsFromComments parseScope allScopes anyScope (Operator.Equals) comments with leafvalue = true }
        NewRule(LeafValueRule(field), options)

    let configRootLeaf processChildConfig (parseScope) allScopes (anyScope) (leaf : Leaf) (comments : string list) =
        match leaf.Key with
        |x when x.StartsWith "alias[" ->
            match getAliasSettingsFromString x with
            |Some (a, rn) ->
                let innerRule = configLeaf processChildConfig parseScope allScopes anyScope leaf comments rn
                AliasRule (a, innerRule)
            |None ->
                let rule = configLeaf processChildConfig parseScope allScopes anyScope leaf comments leaf.Key
                TypeRule (x, rule)
        |x when x.StartsWith "single_alias[" ->
            match getSettingFromString x "single_alias" with
            |Some (a) ->
                let innerRule = configLeaf processChildConfig parseScope allScopes anyScope leaf comments x
                SingleAliasRule (a, innerRule)
            |None ->
                let rule = configLeaf processChildConfig parseScope allScopes anyScope leaf comments leaf.Key
                TypeRule (x, rule)
        |x ->
            let rule = configLeaf processChildConfig parseScope allScopes anyScope leaf comments leaf.Key
            TypeRule (x, rule)

    let configRootNode processChildConfig (parseScope) allScopes (anyScope) (node : Node) (comments : string list) =
        let children = getNodeComments node
        let options = getOptionsFromComments parseScope allScopes anyScope (Operator.Equals) comments
        let innerRules = children |> List.choose (processChildConfig parseScope allScopes anyScope)
        match node.Key with
        |x when x.StartsWith "alias[" ->
            match getAliasSettingsFromString x with
            |Some (a, rn) ->
                let innerRule = configNode processChildConfig parseScope allScopes anyScope node comments rn
                // log "%s %A" a innerRule
                AliasRule (a, innerRule)
            |None ->
                TypeRule (x, NewRule(NodeRule(ValueField(ValueType.Specific (StringResource.stringManager.InternIdentifierToken x)), innerRules), options))
        |x when x.StartsWith "single_alias[" ->
            match getSettingFromString x "single_alias" with
            |Some (a) ->
                let innerRule = configNode processChildConfig parseScope allScopes anyScope node comments x
                SingleAliasRule (a, innerRule)
            |None ->
                TypeRule (x, NewRule(NodeRule(ValueField(ValueType.Specific (StringResource.stringManager.InternIdentifierToken x)), innerRules), options))
        |x ->
            TypeRule (x, NewRule(NodeRule(ValueField(ValueType.Specific (StringResource.stringManager.InternIdentifierToken x)), innerRules), options))

    let rec processChildConfig (parseScope) allScopes (anyScope) ((child, comments) : Child * string list)  =
        match child with
        |NodeC n -> Some (configNode processChildConfig parseScope allScopes anyScope n comments (n.Key))
        |ValueClauseC vc -> Some (configValueClause processChildConfig parseScope allScopes anyScope vc comments)
        |LeafC l -> Some (configLeaf processChildConfig parseScope allScopes anyScope l comments (l.Key))
        |LeafValueC lv -> Some (configLeafValue processChildConfig parseScope allScopes anyScope lv comments)
        |_ -> None

    let processChildConfigRoot (parseScope) (allScopes) (anyScope) ((child, comments) : Child * string list) =
        match child with
        |NodeC n when n.Key == "types" -> None
        |NodeC n -> Some (configRootNode processChildConfig parseScope allScopes anyScope n comments)
        |LeafC l -> Some (configRootLeaf processChildConfig parseScope allScopes anyScope l comments)
        // |LeafValueC lv -> Some (configLeafValue lv comments)
        |_ -> None

    // Types

    let processType (parseScope) (allScopes) (anyScope) (node : Node) (comments : string list) =
        let parseLocalisation ((child : Child), comments : string list) =
            match child with
            |LeafC loc ->
                let required = comments |> List.exists (fun s -> s.Contains "required")
                let optional = comments |> List.exists (fun s -> s.Contains "optional")
                let key = loc.Key
                let value = loc.Value.ToRawString()
                match value.IndexOf "$" with
                | -1 ->
                    Some { name = key; prefix = ""; suffix = ""; required = required; optional = optional; replaceScopes = replaceScopes parseScope comments; explicitField = Some value }
                | dollarIndex ->
                    let prefix = value.Substring(0, dollarIndex)
                    let suffix = value.Substring(dollarIndex + 1)
                    Some { name = key; prefix = prefix; suffix = suffix; required = required; optional = optional; replaceScopes = replaceScopes parseScope comments; explicitField = None }
            |_ -> None
        let parseSubTypeLocalisation (subtype : Node) =
            match subtype.Key.StartsWith("subtype[") with
            |true ->
                match getSettingFromString subtype.Key "subtype" with
                |Some st ->
                    let res = getNodeComments subtype |> List.choose parseLocalisation
                    Some (st, res)
                |_ -> None
            |_ -> None
        let parseSubType ((child : Child), comments : string list) =
            match child with
            |NodeC subtype when subtype.Key.StartsWith "subtype" ->
                let typekeyfilter =
                    match comments |> List.tryFind (fun s -> s.Contains "type_key_filter") with
                    |Some c -> Some (c.Substring(c.IndexOf "=" + 1).Trim())
                    |None -> None
                let pushScope =
                    match comments |> List.tryFind (fun s -> s.Contains("push_scope")) with
                    |Some s -> s.Substring(s.IndexOf "=" + 1).Trim() |> parseScope |> Some
                    |None -> None
                let startsWith =
                    match comments |> List.tryFind (fun s -> s.Contains "starts_with") with
                    |Some c -> Some (c.Substring(c.IndexOf "=" + 1).Trim())
                    |None -> None
                let rules = (getNodeComments subtype |> List.choose (processChildConfig parseScope allScopes anyScope))
                match getSettingFromString (subtype.Key) "subtype" with
                |Some key -> Some { name = key; rules = rules; typeKeyField = typekeyfilter; pushScope = pushScope; localisation = []; startsWith = startsWith }
                |None -> None
            |_ -> None
        let getSkipRootKey (node : Node) =
            let createSkipRoot (s : string) = if s == "any" then SkipRootKey.AnyKey else SkipRootKey.SpecificKey s
            let skipRootKeyLeaves = node.Leafs "skip_root_key" |> List.ofSeq
            match skipRootKeyLeaves with
            | [x] when x.ValueText = "any" -> [SkipRootKey.AnyKey]
            | [x] -> [SkipRootKey.SpecificKey x.ValueText]
            | x::xs ->
                let shouldMatch = x.Operator = Operator.Equals
                eprintfn "gsrk %A %A" shouldMatch (x::xs)
                [SkipRootKey.MultipleKeys ( (x::xs) |> List.map (fun y -> y.ValueText), shouldMatch)]
            | [] -> node.Child "skip_root_key" |> Option.map (fun c -> c.LeafValues |> Seq.map (fun lv -> createSkipRoot (lv.Value.ToRawString())))
                                                    |> Option.defaultValue Seq.empty
                                                    |> Seq.toList

            // match node.Has "skip_root_key", node.TagText "skip_root_key" with
            // |_, "any" -> [SkipRootKey.AnyKey]
            // |true, "" -> node.Child "skip_root_key" |> Option.map (fun c -> c.LeafValues |> Seq.map (fun lv -> createSkipRoot (lv.Value.ToRawString())))
            //                                         |> Option.defaultValue Seq.empty
            //                                         |> Seq.toList
            // |true, x -> [SkipRootKey.SpecificKey x]
            // |false, _ -> []
        let validTypeKeys = [|"name_field"; "type_per_file"; "skip_root_key"; "path"; "path_strict"; "path_file"; "starts_with"; "severity"; "unique"; |]
        let checkTypeChildren (child : Child) =
            match child with
            | LeafC leaf ->
                if Array.contains leaf.Key validTypeKeys
                then ()
                else log (sprintf "Unexpected leaf %s found in type definition at %A" leaf.Key leaf.Position)
            | NodeC node ->
                match node.Key with
                | "localisation" -> ()
                | x when x.StartsWith "subtype" -> ()
                | x -> log (sprintf "Unexpected node %s found in type definition at %A" x node.Position)
            | LeafValueC leafvalue -> log (sprintf "Unexpected leafvalue %s found in type definition at %A" leafvalue.Key leafvalue.Position)
            | ValueClauseC vc -> log (sprintf "Unexpected valueclause found in type definition at %A" vc.Position)
            | CommentC _ -> ()
        match node.Key with
        |x when x.StartsWith("type") ->
            node.All |> List.iter checkTypeChildren
            let typename = getSettingFromString node.Key "type"
            let namefield = if node.Has "name_field" then Some (node.TagText "name_field") else None
            let type_per_file = node.TagText "type_per_file" == "yes"
            let path = (node.TagsText "path") |> List.ofSeq |> List.map (fun s -> s.Replace("game/","").Replace("game\\",""))
            let path_strict = node.TagText "path_strict" == "yes"
            let path_file = if node.Has "path_file" then Some (node.TagText "path_file") else None
            let startsWith = if node.Has "starts_with" then Some (node.TagText "starts_with") else None
            let skiprootkey = getSkipRootKey node
            let subtypes = getNodeComments node |> List.choose parseSubType
            let warningOnly = node.TagText "severity" == "warning"
            let unique = node.TagText "unique" == "yes"
            let localisation = node.Child "localisation" |> Option.map (fun l -> getNodeComments l |> List.choose parseLocalisation) |> Option.defaultValue []
            let subtypelocalisations = node.Child "localisation" |> Option.map (fun l -> l.Children |> List.choose parseSubTypeLocalisation) |> Option.defaultValue []
            let subtypes = subtypes |> List.map (fun st -> let loc = subtypelocalisations |> List.filter (fun (stl, _) -> stl = st.name) |> List.collect snd in {st with localisation = loc})
            let typekeyfilter =
                match comments |> List.tryFind (fun s -> s.Contains "type_key_filter") with
                |Some c ->
                    //log "c %A" c
                    let valid = c.Contains "=" || c.Contains "<>"
                    if valid
                    then
                        let negative = c.Contains "<>"
                        let rhs =
                            if negative
                            then c.Substring(c.IndexOf "<>" + 2).Trim()
                            else c.Substring(c.IndexOf "=" + 1).Trim()
                        let values =
                            match rhs.StartsWith("{") && rhs.EndsWith("}") with
                            |true -> rhs.Trim('{','}') |> (fun s -> s.Split([|' '|])) |> List.ofArray
                            |false -> [rhs]
                        Some (values, negative)
                    else None
                |None -> None
            match typename with
            |Some tn -> Some { name = tn; nameField = namefield; type_per_file = type_per_file; path = path; path_file = path_file; conditions = None; subtypes = subtypes; typeKeyFilter = typekeyfilter; skipRootKey = skiprootkey; warningOnly = warningOnly; path_strict = path_strict; localisation = localisation; startsWith = startsWith; unique = unique}
            |None -> None
        |_ -> None



    let processChildType (parseScope) allScopes (anyScope) ((child, comments) : Child * string list) =
        match child with
        | NodeC n when n.Key == "types" ->
            let inner ((child2, comments2) : Child * string list) =
                match child2 with
                |NodeC n2 -> (processType parseScope allScopes anyScope n2 comments2)
                |_ -> None
            Some (getNodeComments n |> List.choose inner)
        |_ -> None

    let processEnum (node : Node) (comments : string list) =
        match node.Key with
        | x when x.StartsWith("enum") ->
            let enumname = getSettingFromString node.Key "enum"
            let values = node.LeafValues |> List.ofSeq |> List.map (fun lv -> lv.Value.ToString().Trim([|'\"'|]))
            match enumname with
            | Some en ->
                let description =
                    match comments |> List.tryFind (fun s -> s.StartsWith "##") with
                    | Some d -> (d.Trim('#'))
                    | None -> en
                Some ({key = en; values = values; description = description})
            | None -> None
        | _ -> None

    let processChildEnum ((child, comments) : Child * string list) =
        match child with
        | NodeC n when n.Key == "enums" ->
            let inner ((child2, comments2) : Child * string list) =
                match child2 with
                | NodeC n2 -> (processEnum n2 comments2)
                | _ -> None
            Some (getNodeComments n |> List.choose inner)
        | _ -> None

    let processComplexEnum (node : Node) (comments : string list) =
        match node.Key with
        | x when x.StartsWith("complex_enum") ->
            let enumname = getSettingFromString node.Key "complex_enum"
            let path = (node.TagText "path").Replace("game/","").Replace("game\\","")
            let nametree = node.Child "name"
            let start_from_root = node.TagText "start_from_root" == "yes"
            match (enumname, nametree) with
            | Some en, Some nt ->
                let description =
                    match comments |> List.tryFind (fun s -> s.StartsWith "##") with
                    | Some d -> (d.Trim('#'))
                    | None -> en
                Some {name = en; path = path; nameTree = nt; start_from_root = start_from_root; description = description}
            | _ -> None
        | _ -> None

    let processComplexChildEnum ((child, comments) : Child * string list) =
        match child with
        |NodeC n when n.Key == "enums" ->
            let inner ((child2, comments2) : Child * string list) =
                match child2 with
                |NodeC n2 -> (processComplexEnum n2 comments2)
                |_ -> None
            Some (getNodeComments n |> List.choose inner)
        |_ -> None


    let processValue (node : Node) (comments : string list) =
        match node.Key with
        |x when x.StartsWith("value") ->
            let enumname = getSettingFromString node.Key "value"
            let values = node.LeafValues |> List.ofSeq |> List.map (fun lv -> lv.Value.ToString().Trim([|'\"'|]))
            match enumname with
            |Some en -> Some (en, values)
            |None -> None
        |_ -> None

    let processChildValue ((child, comments) : Child * string list) =
        match child with
        |NodeC n when n.Key == "values" ->
            let inner ((child2, comments2) : Child * string list) =
                match child2 with
                |NodeC n2 -> (processValue n2 comments2)
                |_ -> None
            Some (getNodeComments n |> List.choose inner)
        |_ -> None



    let replaceSingleAliases (rules : RootRule<_> list) =
        let mutable singlealiases = rules |> List.choose (function |SingleAliasRule (name, inner) -> Some (SingleAliasRule (name, inner)) |_ -> None) //|> Map.ofList
        let singlealiasesmap() = singlealiases |> List.choose (function |SingleAliasRule (name, inner) -> Some (name, inner) |_ -> None) |> Map.ofList

        let rec cataRule rule : NewRule<_> =
            match rule with
            | (NodeRule (l, r), o) -> (NodeRule (l, r |> List.map cataRule), o)
            | (ValueClauseRule (r), o) -> (ValueClauseRule (r |> List.map cataRule), o)
            | (SubtypeRule (a, b, i), o) -> (SubtypeRule(a, b, (i |> List.map cataRule)), o)
            | (LeafRule (l, SingleAliasField name), o) ->
                match singlealiasesmap() |> Map.tryFind name with
                | Some (LeafRule (al, ar), ao) ->
                    log (sprintf "Replaced single alias leaf %A %s with leaf %A" (l |> function |ValueField (Specific x) -> StringResource.stringManager.GetStringForIDs x |_ -> "") name (al |> function |ValueField (Specific x) -> StringResource.stringManager.GetStringForIDs x |_ -> ""))
                    LeafRule (l, ar), o
                | Some (NodeRule (al, ar), ao) ->
                    log (sprintf "Replaced single alias leaf %A %s with node %A" (l |> function |ValueField (Specific x) -> StringResource.stringManager.GetStringForIDs x |_ -> "") name (al |> function |ValueField (Specific x) -> StringResource.stringManager.GetStringForIDs x |_ -> ""))
                    NodeRule (l, ar), o
                | x ->
                    log (sprintf "Failed to find defined single alias %s when replacing single alias leaf %A. Found %A" name (l |> function |ValueField (Specific x) -> StringResource.stringManager.GetStringForIDs x |_ -> "") x)
                    rule
            | _ -> rule
        let singlealiasesmapper =
            function
            | SingleAliasRule (name, rule) -> SingleAliasRule(name, cataRule rule)
            | x -> x
        let mutable final = singlealiases
        let mutable i = 0
        let mutable first = true
        let ff() =
            i <- i + 1
            let before = final
            final <- final |> List.map singlealiasesmapper
            singlealiases <- final
            first <- false
            before = final || i > 10
        while (not (ff())) do ()

        let rulesMapper =
            function
            | TypeRule (name, rule) -> TypeRule (name, cataRule rule)
            | AliasRule (name, rule) -> AliasRule (name, cataRule rule)
            | SingleAliasRule (name, rule) -> SingleAliasRule(name, cataRule rule)
        rules |> List.map rulesMapper


    let replaceColourField (rules : RootRule<_> list) =

        let rec cataRule rule : NewRule<_> list =
            match rule with
            | LeafRule (l, MarkerField (ColourField)), o  ->
                [
                    NodeRule((l), [LeafValueRule(ValueField(ValueType.Float(-256.0, 256.0))), { defaultOptions with min = 3; max = 3 } ]), o
                ]
            | LeafRule (l, MarkerField (IRCountryTag)), o  ->
                [
                    LeafRule(l, ValueField(ValueType.Enum "country_tags")), o
                    LeafRule(l, VariableGetField "dynamic_country_tag"), o
                ]
            | LeafRule (MarkerField (IRCountryTag), r), o  ->
                [
                    LeafRule(ValueField(ValueType.Enum "country_tags"), r), o
                    LeafRule(VariableGetField "dynamic_country_tag", r), o
                ]
            | NodeRule (MarkerField (IRCountryTag), r), o ->
                [
                    NodeRule(ValueField(ValueType.Enum "country_tags"), r |> List.collect cataRule), o
                    NodeRule(VariableGetField "dynamic_country_tag", r |> List.collect cataRule), o
                ]
            | NodeRule (l, r), o ->
                [NodeRule(l, r |> List.collect cataRule), o]
            | ValueClauseRule (r), o -> [ValueClauseRule (r |> List.collect cataRule), o]
            | (SubtypeRule (a, b, i), o) -> [(SubtypeRule(a, b, (i |> List.collect cataRule)), o)]
            | _ -> [rule]
        let rulesMapper =
            function
            | TypeRule (name, rule) -> cataRule rule |> List.map (fun x -> TypeRule (name, x))
            | AliasRule (name, rule) -> cataRule rule |> List.map (fun x ->  AliasRule (name, x))
            | SingleAliasRule (name, rule) -> cataRule rule |> List.map (fun x ->  SingleAliasRule(name, x))
        rules |> List.collect rulesMapper

    let replaceValueMarkerFields (rules : RootRule<_> list) =
        let rec cataRule rule : NewRule<_> list =
            match rule with
            | LeafRule (ValueScopeMarkerField (i,m), ValueScopeMarkerField (i2,m2)), o when not o.comparison ->
                [
                    LeafRule(ValueScopeField(i, m), ValueScopeField(i2, m2)), o
                    LeafRule(ValueScopeField(i, m), SingleAliasField("formula")), o
                    LeafRule(ValueScopeField(i, m), SingleAliasField("range")), o
                ]
            | LeafRule (ValueScopeMarkerField (i,m), ValueScopeMarkerField (i2,m2)), o when o.comparison ->
                [
                    LeafRule(ValueScopeField(i, m), ValueScopeField(i2, m2)), o
                ]
            | LeafRule (l, ValueScopeMarkerField (i2,m2)), o when not o.comparison ->
                [
                    LeafRule(l, ValueScopeField(i2, m2)), o
                    LeafRule(l, SingleAliasField("formula")), o
                    LeafRule(l, SingleAliasField("range")), o
                ]
            | LeafRule (l, ValueScopeMarkerField (i2,m2)), o when o.comparison ->
                [
                    LeafRule(l, ValueScopeField(i2, m2)), o
                ]
            | LeafRule (ValueScopeMarkerField (i,m), r), o ->
                [LeafRule(ValueScopeField(i, m), r), o]
            | NodeRule (ValueScopeMarkerField (i,m), r), o ->
                [NodeRule(ValueScopeField(i, m), r |> List.collect cataRule), o]
            | NodeRule (l, r), o ->
                [NodeRule(l, r |> List.collect cataRule), o]
            | ValueClauseRule (r), o -> [ValueClauseRule (r |> List.collect cataRule), o]
            | (SubtypeRule (a, b, i), o) -> [(SubtypeRule(a, b, (i |> List.collect cataRule)), o)]
            | _ -> [rule]
        let rulesMapper =
            function
            | TypeRule (name, rule) -> cataRule rule |> List.map (fun x -> TypeRule (name, x))
            | AliasRule (name, rule) -> cataRule rule |> List.map (fun x ->  AliasRule (name, x))
            | SingleAliasRule (name, rule) -> cataRule rule |> List.map (fun x ->  SingleAliasRule(name, x))
        rules |> List.collect rulesMapper

    let processConfig (parseScope) (allScopes) (anyScope) (node : Node) =
        let nodes = getNodeComments node
        let rules = nodes |> List.choose (processChildConfigRoot parseScope allScopes anyScope)
        let types = nodes |> List.choose (processChildType parseScope allScopes anyScope) |> List.collect id
        let enums = nodes |> List.choose processChildEnum |> List.collect id
        let complexenums = nodes |> List.choose processComplexChildEnum |> List.collect id
        let values = nodes |> List.choose processChildValue |> List.collect id
        rules, types, enums, complexenums, values

    let parseConfig (parseScope) (allScopes) (anyScope) filename fileString =
        //log "parse"
        let parsed = CKParser.parseString fileString filename
        match parsed with
        |Failure(e, _, _) -> log (sprintf "config file %s failed with %s" filename e); ([], [], [], [], [])
        |Success(s,_,_) ->
            //log "parsed %A" s
            let root = simpleProcess.ProcessNode() "root" (mkZeroFile filename) (s)
            //log "processConfig"
            processConfig parseScope allScopes anyScope root
    let parseConfigs (parseScope) (allScopes) (anyScope) (files : (string * string) list)  =
        let rules, types, enums, complexenums, values =
            files |> List.map (fun (filename, fileString) -> parseConfig parseScope allScopes anyScope filename fileString)
              |> List.fold (fun (rs, ts, es, ces, vs) (r, t, e, ce, v) -> r@rs, t@ts, e@es, ce@ces, v@vs) ([], [], [], [], [])
        let rules = rules |> replaceValueMarkerFields |> replaceSingleAliases |> replaceColourField
        // File.AppendAllText ("test.test", sprintf "%O" rules)
        rules, types, enums, complexenums, values


    let createStarbase =
        let owner = NewRule (LeafRule(specificField "owner", ScopeField Scope.Any), requiredSingle)
        let size = NewRule (LeafRule(specificField "size", ValueField(ValueType.Enum "size")), requiredSingle)
        let moduleR = NewRule (LeafRule(specificField "module", ValueField(ValueType.Enum "module")), optionalMany)
        let building = NewRule (LeafRule(specificField "building", ValueField(ValueType.Enum "building")), optionalMany)
        let effect = NewRule (NodeRule(specificField "effect", [(LeafRule (AliasField "effect", AliasField "effect")), optionalMany]), { optionalSingle with replaceScopes = Some { froms = None; root = Some (Scope.Country); this = Some (Scope.Country); prevs = None }})
        let rule = NewRule (NodeRule(specificField "create_starbase", [owner; size; moduleR; building; effect]), optionalMany)
        rule
    let createStarbaseAlias = AliasRule ("effect", createStarbase)
    let createStarbaseEnums =
        [("size", ("size", ["medium"; "large"]));
         ("module", ("module", ["trafficControl"]));
         ("building", ("building", ["crew"]))]
        |> Map.ofList
    let createStarbaseTypeDef =
        {
            name = "create_starbase"
            nameField = None
            path = ["events"]
            path_strict = false
            path_file = None
            conditions = None
            subtypes = []
            typeKeyFilter = None
            skipRootKey = []
            warningOnly = false
            type_per_file = false
            localisation = []
            startsWith = None
            unique = false
        }
// # strategic_resource: strategic resource, deprecated, strategic resource used by the building.
// # allow: trigger to check for allowing construction of building.
// # prerequisites: Tech requirements for building.
// # empire_unique: boolean, can only build one if set to true.
// # cost: resource table, cost of building.
// # is_orbital: boolean, can only be built in orbital station.
// # modifier: modifier, deprecated, applies a modifier to planet; use planet_modifier instead.
// # planet_modifier, country_modifier, army_modifier: applies modifier to planet/country/armies
// # triggered_planet_modifier = { key (optional), potential (scope: planet), modifier }: applies conditional modifier to planet
// # base_buildtime: int, number of days for construction.
// # requires_pop, boolean, building will require a pop for production.
// # required_resources, resource table, required resources for production.
// # produced_resources, resource table, produced resources in production.
// # upgrades, buildings list, buildings this building can upgrade into.
// # is_listed, boolean, toggles if this building is shown in the non-upgrade buildable list.
// # planet_unique, toggles if one can build multiple of this type on a single planet.
// # ai_weight, weight for AI, default is set to one, weight set to 0 means that AI will never build it
// # is_colony: trigger to check if the building is a colony shelter for country (scope: country, from: planet). default: "always = no"
// # active: trigger to check if a building can be active with a given pop worker (scope: pop) if you add a trigger here, you should also add the requirements in the description
// # show_tech_unlock_if: trigger to show this building only conditionally in the technology screen. scope: country. default: { always = yes }
// # planet_modifier_with_pop_trigger = { key (optional), potential (scope: pop), modifier }: applies modifier to pops on planet that satisfy condition in trigger

    let building =
        let inner =
            [
                NewRule (LeafRule(specificField "allow", ValueField ValueType.Scalar), requiredSingle)
                NewRule (LeafRule(specificField "empire_unique", ValueField ValueType.Bool), optionalSingle)
            ]
        NewRule(NodeRule(specificField "building", inner), optionalMany)

    // formation_priority = @corvette_formation_priority
    // max_speed = @speed_very_fast
    // acceleration = 0.35
    // rotation_speed = 0.1
    // collision_radius = @corvette_collision_radius
    // max_hitpoints = 300
    // modifier = {
    // 	ship_evasion_add = 60
    // }
    // size_multiplier = 1
    // fleet_slot_size = 1
    // section_slots = { "mid" = { locator = "part1" } }
    // num_target_locators = 2
    // is_space_station = no
    // icon_frame = 2
    // base_buildtime = 60
    // can_have_federation_design = yes
    // enable_default_design = yes	#if yes, countries will have an auto-generated design at start

    // default_behavior = swarm

    // prerequisites = { "tech_corvettes" }

    // combat_disengage_chance = 1.75

    // has_mineral_upkeep = yes
    // class = shipclass_military
    // construction_type = starbase_shipyard
    // required_component_set = "power_core"
    // required_component_set = "ftl_components"
    // required_component_set = "thruster_components"
    // required_component_set = "sensor_components"
    // required_component_set = "combat_computers"
    let shipsize =
        let inner =
            [
                NewRule(LeafRule(specificField "formation_priority", defaultInt), optionalSingle);
                NewRule(LeafRule(specificField "max_speed", defaultFloat), requiredSingle);
                NewRule(LeafRule(specificField "acceleration", defaultFloat), requiredSingle);
                NewRule(LeafRule(specificField "rotation_speed", defaultFloat), requiredSingle);
                NewRule(LeafRule(specificField "collision_radius", defaultFloat), optionalSingle);
                NewRule(LeafRule(specificField "max_hitpoints", defaultInt), requiredSingle);
                NewRule(NodeRule(specificField "modifier", []), optionalSingle);
                NewRule(LeafRule(specificField "size_multiplier", defaultInt), requiredSingle);
                NewRule(LeafRule(specificField "fleet_slot_size", defaultInt), requiredSingle);
                NewRule(NodeRule(specificField "section_slots", []), optionalSingle);
                NewRule(LeafRule(specificField "num_target_locators", defaultInt), requiredSingle);
                NewRule(LeafRule(specificField "is_space_station", ValueField ValueType.Bool), requiredSingle);
                NewRule(LeafRule(specificField "icon_frame", defaultInt), requiredSingle);
                NewRule(LeafRule(specificField "base_buildtime", defaultInt), requiredSingle);
                NewRule(LeafRule(specificField "can_have_federation_design", ValueField ValueType.Bool), requiredSingle);
                NewRule(LeafRule(specificField "enable_default_design", ValueField ValueType.Bool), requiredSingle);
                NewRule(LeafRule(specificField "default_behavior", TypeField (Simple "ship_behavior")), requiredSingle);
                NewRule(NodeRule(specificField "prerequisites", []), optionalSingle);
                NewRule(LeafRule(specificField "combat_disengage_chance", defaultFloat), optionalSingle);
                NewRule(LeafRule(specificField "has_mineral_upkeep", ValueField ValueType.Bool), requiredSingle);
                NewRule(LeafRule(specificField "class", ValueField ValueType.Scalar), requiredSingle);
                NewRule(LeafRule(specificField "construction_type", ValueField ValueType.Scalar), requiredSingle);
                NewRule(LeafRule(specificField "required_component_set", ValueField ValueType.Scalar), requiredSingle);
            ]
        NewRule(NodeRule(specificField "ship_size", inner), optionalMany)

    let shipBehaviorType =
        {
            name = "ship_behavior";
            nameField = Some "name";
            path = ["common/ship_behaviors"];
            conditions = None;
            subtypes = [];
            typeKeyFilter = None
            skipRootKey = []
            warningOnly = false
            path_strict = false
            path_file = None
            type_per_file = false
            localisation = []
            startsWith = None
            unique = false
        }
    let shipSizeType =
        {
            name = "ship_size";
            path = ["common/ship_sizes"];
            nameField = None;
            conditions = None;
            subtypes = [];
            typeKeyFilter = None
            skipRootKey = []
            warningOnly = false
            path_strict = false
            path_file = None
            type_per_file = false
            localisation = []
            startsWith = None
            unique = false
        }
//  type[ship_behavior] = {
//      path = "game/common/ship_behaviors"
//      name_field = "name"
//  }
//  type[leader_trait] = {
//      path = "game/common/traits"
//      conditions = {
//          leader_trait = yes
//      }
//  }
//  type[species_trait] = {
//      path = "game/common/traits"
//  }
