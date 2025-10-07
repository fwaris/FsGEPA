namespace FsGepa.Run
open FSharp.Control
open FsGepa

module Selection = 
    ///rank each sys id for each task (multiple sys ids can have the same rank for the same task)
    let rankByTask byTask = 
        byTask 
        |> List.map(fun (i,xs) -> 
            i, 
            xs 
            |> Set.toList 
            |> List.mapi (fun rank (_,ys) -> 
                ys 
                |> Set.toList 
                |> List.map (fun sid -> sid,rank)) 
                |> List.collect id 
                |> Map.ofList)

    let dominated (ranksByTask:list<int * Map<string,int>>) (a,b) =
        let ranks = 
            ranksByTask 
            |> List.map(fun (i,m) ->  m.[a], m.[b])
            |> List.map (fun (ra,rb) -> if ra = rb then None elif ra > rb then Some a else Some b)
            |> List.choose id
            |> set
        if ranks.Count = 1 then 
            let x = ranks.MinimumElement
            if x = a 
            then Some b //a dominates b so prune b 
            else Some a
        else 
            None // neither a nor b dominates the other

    let prune (ranksByTask:list<int * Map<string,int>>) acc (_,(scr:float,xs:Set<string>)) = 
        let pairs = let xs = Set.toList xs in List.allPairs xs xs |> List.filter (fun (a,b) -> a <> b)
        (acc,pairs) 
        ||> List.fold(fun acc (a,b) -> 
            match dominated ranksByTask (a,b) with 
            | Some x -> acc |> Set.add x 
            | _ -> acc)

    let paretoPool grun = async {
        let byTask = 
            grun.candidates 
            |> List.collect (fun c -> c.evals |> List.map (fun e -> c.id,(e.index,e.eval.score)))
            |> List.groupBy (fun (sid,(i,scr)) -> i)
            |> List.map (fun (i,xs) -> 
                let prioritySet = 
                    xs 
                    |> List.groupBy (fun (_,(_,scr)) -> scr) 
                    |> List.map (fun (s,xs) -> s, xs |> List.map fst |> set)                    
                    |> set                
                i,prioritySet)
            |> List.filter (fun (i,xs) -> not xs.IsEmpty)
        let frontier = byTask |> List.map(fun (i,ts) -> i, Set.maxElement ts)
        let rankedByTask = rankByTask byTask
        let toPrune = (Set.empty,frontier) ||> List.fold (prune rankedByTask)
        let frontierSet = frontier |> Seq.collect (fun (_,(_,xs)) -> xs) |> set
        let candidates = Set.difference frontierSet toPrune
        return grun.candidates |> List.filter (fun c -> candidates.Contains c.id)
    }

