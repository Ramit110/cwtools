namespace CWTools.Validation
open CWTools.Validation.ValidationCore
open CWTools.Process.STLProcess
open CWTools.Process
open CWTools.Process.ProcessCore
open CWTools.Parser
open CWTools.Process.STLScopes
open CWTools.Common
open CWTools.Common.STLConstants
open DotNet.Globbing
open CWTools.Games
open Newtonsoft.Json.Linq
open CWTools.Utilities.Utils
open STLValidation

module STLEventValidation =
    type 'a Node = 'a * 'a list
    type 'a Edge = 'a * 'a

    type 'a Graph = 'a list * 'a Edge list

    type 'a AdjacencyGraph = 'a Node list   
    type Color = White = 0 | Gray = 1 | Black = 2

    let graph2AdjacencyGraph ((ns, es) : 'a Graph) : 'a AdjacencyGraph = 
        let nodeMap = ns |> List.map(fun n -> n, []) |> Map.ofList
        (nodeMap,es) 
        ||> List.fold(fun map (a,b) -> map |> Map.add a (b::map.[a]) |> Map.add b (a::map.[b]))
        |> Map.toList


    let depthFirstOrder (g : 'a AdjacencyGraph) start = 
        let nodes = g |> Map.ofList
        let color = g |> List.map(fun (v,_) -> v, Color.White) |> Map.ofList |> ref
        let pi = ref [start]
        //eprintfn "%A" g

        let rec dfs u = 
            color := Map.add u Color.Gray !color
            for v in nodes.[u] do
                //eprintfn "%A" v
                if (!color).[v] = Color.White then
                    pi := (v::!pi)
                    dfs v
            color := Map.add u Color.Black !color

        dfs start
        !pi |> List.rev
    let connectedComponents (g : 'a AdjacencyGraph) =
        match g with
        |[] -> []
        |g -> 
            let nodes = g |> List.map fst |> Set.ofList
            let start = g |> List.head |> fst
            let rec loop acc g start nodes = 
                let dfst = depthFirstOrder g start |> Set.ofList
                let nodes' = Set.difference nodes dfst 
                if Set.isEmpty nodes' then
                    g::acc
                else
                    // once we have the dfst set we can remove those nodes from the graph and
                    // add them to the solution and continue with the remaining nodes
                    let (cg,g') = g |> List.fold(fun (xs,ys) v -> if Set.contains (fst v) dfst then (v::xs,ys) else (xs,v::ys)) ([],[])
                    let start' = List.head g' |> fst
                    loop (cg::acc) g' start' nodes'
            loop [] g start nodes
            
    let findAllReferencedEvents (projects : Node list) (event : Event) =
        let eventEffectKeys = ["ship_event"; "pop_event"; "fleet_event"; "pop_faction_event"; "country_event"; "planet_event"]
        let fNode = (fun (x : Node) children ->
                        match x.Key with
                        |k when eventEffectKeys |> List.exists (fun f -> f == k) ->
                            x.TagText "id" :: children
                        |_ -> children)
        let fpNode = (fun (x : Node) children ->
                        match x.Key with
                        |"enable_special_project" -> [x.TagText "name"]
                        |_ -> children)
        let fCombine = (@)
        let directCalls = event.Children |> List.collect (foldNode2 fNode fCombine []) // |> List.fold (@) []
        let referencedProjects = event.Children |> List.collect (foldNode2 fpNode fCombine [])
        //eprintfn "%s projs %A" (event.ID) referencedProjects
        let getProjectTarget (n : Node) =
            let projectKeys = ["on_success"; "on_fail"; "on_start"; "on_progress_25"; "on_progress_50"; "on_progress_75";]
            n.Children |> List.filter (fun c -> List.contains c.Key projectKeys) |> List.collect (foldNode2 fNode fCombine [])
        let projectTargets = projects |> List.filter (fun p -> List.contains (p.TagText "key") referencedProjects) |> List.collect getProjectTarget
        //eprintfn "%s proj targets %A" (event.ID) projectTargets
        directCalls @ projectTargets
    
    let findAllUsedEventTargets (event : Event) =
        let fNode = (fun (x : Node) children ->
                        let targetFromString (k : string) = k.Substring(13).Split('.').[0]
                        let inner (leaf : Leaf) = if leaf.Value.ToRawString().StartsWith("event_target:") then Some (leaf.Value.ToRawString() |> targetFromString) else None
                        match x.Key with
                        |k when k.StartsWith("event_target:") -> 
                           targetFromString k :: ((x.Values |> List.choose inner) @ children)     
                        |_ ->                      
                            ((x.Values |> List.choose inner) @ children)    
                        
                        )
        let fCombine = (@)
        event |> (foldNode2 fNode fCombine []) |> Set.ofList

    let findAllSavedEventTargets (event : Event) =
        let fNode = (fun (x : Node) children ->
                        let inner (leaf : Leaf) = if leaf.Key == "save_event_target_as" then Some (leaf.Value.ToRawString()) else None
                        (x.Values |> List.choose inner) @ children
                        )
        let fCombine = (@)
        event |> (foldNode2 fNode fCombine []) |> Set.ofList

    let findAllExistsEventTargets (event : Event) =
        let fNode = (fun (x : Node) children ->
                        let inner (leaf : Leaf) = if leaf.Key == "exists" && leaf.Value.ToRawString().StartsWith("event_target:") then Some (leaf.Value.ToRawString().Substring(13).Split('.').[0]) else None
                        (x.Values |> List.choose inner) @ children
                        )
        let fCombine = (@)
        event |> (foldNode2 fNode fCombine []) |> Set.ofList

    let findAllSavedGlobalEventTargets (event : Node) =
        let fNode = (fun (x : Node) children ->
                        let inner (leaf : Leaf) = if leaf.Key == "save_global_event_target_as" then Some (leaf.Value.ToRawString()) else None
                        (x.Values |> List.choose inner) @ children
                        )
        let fCombine = (@)
        event |> (foldNode2 fNode fCombine []) |> Set.ofList

    let addScriptedEffectTargets (effects : ScriptedEffect list) ((e, s, u, r, x) : Event * Set<string> * Set<string> * string list * Set<string>) = 
        let fNode = (fun (x : Node) (s, u) ->
                        let inner (leaf : Leaf) = effects |> List.tryFind (fun e -> leaf.Key == e.Name) |> Option.map (fun e -> e.SavedEventTargets, e.UsedEventTargets)
                        x.Values |> List.choose inner |> List.fold (fun (s, u) (s2, u2) -> s@s2, u@u2) (s, u))
        let fCombine = (fun (s, u) (s2, u2) -> s@s2, u@u2)
        let s2, u2 = foldNode2 fNode fCombine (s |> Set.toList, u |> Set.toList) e
        (e, s2 |> Set.ofList, u2 |> Set.ofList, r, x)

    let checkEventChain (effects : ScriptedEffect list) (projects : Node list) (globals : Set<string>) (events : Event list) =
        let mutable current = events |> List.map (fun e -> (e, findAllSavedEventTargets e, findAllUsedEventTargets e, findAllReferencedEvents projects e, findAllExistsEventTargets e))
                                        //|> (fun f -> eprintfn "%A" f; f)
                                        |> List.map (addScriptedEffectTargets effects)
                                        |> List.map (fun (e, s, u, r, x) -> e, s, Set.difference u s, r, x)
        let getRequiredTargets (ids : string list) =
            let ret = ids |> List.collect (fun x -> current |> List.pick (fun (e2,_ , u2, _, _) -> if e2.ID = x then Some (Set.toList u2) else None)) |> Set.ofList
           // eprintfn "%A" ret
            ret
        let getExistsTargets (ids : string list) = 
            let ret = ids |> List.map (fun x -> current |> List.pick (fun (e2,_ , _, _, x2) -> if e2.ID = x then Some (Set.toList x2) else None)) 
                            |> (fun f -> if List.isEmpty f then Set.empty else f |> List.map Set.ofList |> List.reduce (fun s n -> Set.intersect s (n))) 
            // eprintfn "%A" ret
            ret

        let update (e, s, u, r, x) =
            e,
            s,
            Set.difference (Set.union u (getRequiredTargets r)) s,
            r,
            Set.union x (getExistsTargets r)
        let mutable i = 0

        let step (es) = 
            //eprintfn "%A" current
            i <- i + 1
            let before = current
            current <- es |> List.map update
            current = before || i > 10
        while (not(step current)) do ()
        //current |> List.iter (fun (e, s, u, r) -> eprintfn "event %s has %A and needs %A" (e.ID) s u)

        // let getSourceSetTargets (ids : string list) =
        //     let inner = (fun (x : string) -> current |> List.pick (fun (e2, s2, _, _) -> if e2.ID = x then Some (s2) else None))
        //     match ids with
        //     |[] -> Set.empty
        //     | xs -> xs |> List.map inner |> List.reduce (Set.intersect)
        let getSourceSetTargets (id : string) =
            let inner = (fun (x : string) -> current |> List.choose (fun (e2, s2, _, r2, _) -> if List.contains x r2 && e2.ID <> x then Some (s2) else None))
            inner id

        let getSourceExistsTargets (id : string) =
            let inner = (fun (x : string) -> current |> List.choose (fun (e2, _, _, r2, x2) -> if List.contains x r2 && e2.ID <> x then Some (x2) else None))
            inner id

        let down ((e : Event), s, u, r, x) =
            e,
            Set.union s (getSourceSetTargets e.ID |> (fun f -> if List.isEmpty f then Set.empty else List.reduce (Set.intersect) f)),
            u,
            r,
            Set.union x (getSourceExistsTargets e.ID |> (fun f -> if List.isEmpty f then Set.empty else List.reduce (Set.intersect) f))
        let mutable i = 0

        let step (es) = 
            //eprintfn "%A" current
            i <- i + 1
            let before = current
            current <- es |> List.map down
            current = before || i > 10
        while (not(step current)) do ()
        //current |> List.iter (fun (e, s, u, r) -> eprintfn "event %s has %A and needs %A" (e.ID) s u)
       // current |> List.iter (fun (e, s, u, r) -> eprintfn "event %s is missing %A" (e.ID) (Set.difference u s))
        let missing = current |> List.filter (fun (e, s, u, r, x) -> not(Set.difference (Set.difference u (Set.union s x)) globals |> Set.isEmpty))
        let maybeMissing = current |> List.filter (fun (e, s, u, r, x) -> 
                                                    not(Set.difference (Set.difference u s) globals |> Set.isEmpty)
                                                    && (Set.difference (Set.difference u (Set.union s x)) globals |> Set.isEmpty))
        let createError (e : Event, s, u, _, _) = 
             let needed = Set.difference u s |> Set.toList |> String.concat ", "
             Invalid [inv (ErrorCodes.UnsavedEventTarget (e.ID) needed) e]
        let createWarning (e : Event, s, u, _, _) = 
             let needed = Set.difference u s |> Set.toList |> String.concat ", "
             Invalid [inv (ErrorCodes.MaybeUnsavedEventTarget (e.ID) needed) e]
        missing <&!&> createError
        <&&>
        (maybeMissing <&!&> createWarning)


            

    let getEventChains (reffects : Effect list) (os : EntitySet) (es : EntitySet) =
        let seffects = reffects |> List.choose (function | :? ScriptedEffect as e -> Some e |_ -> None)
        let allevents = os.GlobMatchChildren("**/events/*.txt") |> List.choose (function | :? Event as e -> Some e |_ -> None)
        let events = es.GlobMatchChildren("**/events/*.txt") |> List.choose (function | :? Event as e -> Some e |_ -> None)
        let projects = os.GlobMatchChildren("**/common/special_projects/*.txt") @ es.GlobMatchChildren("**/common/special_projects/*.txt")
        let effects = os.AllEffects @ es.AllEffects
        let globalScriptedEffects = seffects |> List.collect (fun se -> se.GlobalEventTargets) |> Set.ofList
        let globals = Set.union globalScriptedEffects (effects |> List.map findAllSavedGlobalEventTargets |> List.fold (Set.union) (Set.empty))
        //eprintfn "%s" (globals |> Set.toList |> String.concat ", ")
        let chains = events |> List.collect (fun (event) -> findAllReferencedEvents projects event |> List.map (fun f -> event.ID, f))
                    //|> (fun f -> eprintfn "%A" f; f)
                    |> List.collect(fun (s, t) -> [s,t; t,s])
                    //|> (fun f -> eprintfn "%A" f; f)
                    |> (fun es -> (graph2AdjacencyGraph (events |> List.map (fun f -> f.ID), es)))
                    |> connectedComponents
                    //|> (fun f -> eprintfn "%A" f; f)
                    |> List.map (fun set -> set |> List.map (fun (event, targets) -> events |> List.find (fun e -> e.ID = event)))
        chains <&!&> checkEventChain seffects projects globals
        
        

