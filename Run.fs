namespace FsGepa.Run
open FSharp.Control
open FsGepa

type GrTask<'a,'b> = {
    task : GeTask<'a,'b>
    evalCache : Eval
}

type GrSystem<'a,'b> = {
    id : string
    parent : string option
    sys : GeSystem<'a,'b>
    avgScore : float
}

type GrRun<'a,'b> = {
    count : int
    candidates : GrSystem<'a, 'b> list
    cfg : Config
    tasksPareto : seq<int * GeTask<'a,'b>>
    tasksFeedback : GeTask<'a,'b> seq
    comboSet : Set<Set<string>>
}

module Run =

    let runTask cfg sys ((i,task):int*_) =  
        async {
            let! o = sys.flow cfg sys.modules task.input
            return (i,task),o
        }

    let evalTask cfg ((i,t),o) = 
        async {
            let! e = t.eval cfg o
            return i,e
        }

    let score cfg sys tasks = 
        tasks 
        |> AsyncSeq.ofSeq
        |> AsyncSeq.mapAsync (runTask cfg sys)
        |> AsyncSeq.mapAsync (evalTask cfg)

    let filterEval mid (i,eval) = 
        { eval with 
            traces = 
                eval.traces 
                |> List.filter (fun t -> t.moduleId = mid)
        }

    let filterEvals cfg moduleId = List.map (filterEval moduleId)

    let setPrompt grSys m prompt =
        {grSys with 
            id = Utils.newId()
            parent = Some grSys.id
            sys = {grSys.sys with 
                    modules = 
                        grSys.sys.modules 
                        |> Map.map(fun k v -> 
                            if k = m.moduleId then {v with prompt=prompt} else v)  
                  }
        }

    let sample cfg tasks = 
        let frac = float cfg.miniBatch / float cfg.totalFeedbackSize
        tasks |> Seq.filter(fun _ -> Utils.rng.NextDouble() <= frac) |> Seq.indexed |> Seq.toList

    let scoreCandidates cfg candidates tasksPareto=
        candidates
        |> AsyncSeq.ofSeq
        |> AsyncSeq.map (fun grsys -> 
            let scores = 
                score cfg grsys.sys tasksPareto 
                |> AsyncSeq.toBlockingSeq
                |> Seq.toList
            grsys,scores
        )
        |> AsyncSeq.toBlockingSeq
        |> Seq.toList

    let removeSys (ts:list<int * (Set<float * Set<string>>)>) sid = 
        ts 
        |> List.map(fun (i,(prioritySet)) -> 
            i,
            prioritySet |> Set.map (fun (scr,xs) -> scr, xs |> Set.remove sid))

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

    let selectCandidate grun = async {
        let scores = scoreCandidates grun.cfg grun.candidates grun.tasksPareto
        let scores = scores |> List.map(fun (s,xs) -> s.id, xs |> List.map(fun (i,e) -> i,e.score))
        let byTask = 
            scores 
            |> List.collect (fun (s,xs) -> xs |> List.map (fun y -> s,y))
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
        let candidates = Set.difference frontierSet toPrune |> Seq.toList
        let selected = randSelect candidates
        let selectedSys = grun.candidates |> List.find (fun x -> x.id = selected)
        return selectedSys
    }

    let selectModule cfg grSys = grSys.sys.modules |> Map.toList |> List.map snd |> Utils.randSelect 

    ///single step of reflective prompt optimizer process
    let stepReflective grun = async {
        let! candidate = selectCandidate grun
        let tasksMB = grun.tasksFeedback |> sample grun.cfg        
        let m = selectModule grun.cfg candidate
        let evals = score grun.cfg candidate.sys tasksMB
                    |> AsyncSeq.toBlockingSeq
                    |> Seq.toList
        let avgScore = evals |> List.averageBy (fun (i,x) -> x.score)
        let mEvals = filterEvals grun.cfg m.moduleId evals
        let! prompt = Llm.Llm.updatePrompt grun.cfg m.prompt mEvals
        let grSys' = setPrompt candidate m prompt
        let evals' = score grun.cfg grSys'.sys tasksMB
                     |> AsyncSeq.toBlockingSeq
                     |> Seq.toList
        let avgScore' = evals' |> List.averageBy (fun (i,x) -> x.score)
        return
            if avgScore' >= avgScore then 
                {grun with candidates = grSys' :: grun.candidates}
            else 
                grun
     }

    let rec samplePair grun attempts = 
        if attempts > 0 then 
            let a = grun.candidates |> randSelect
            let b = grun.candidates |> randSelect
            if a.id <> b.id then 
                Some (a,b)
            else
                samplePair grun (attempts - 1)
        else
            None

    let ancestors grun a =
        let pmap = grun.candidates |> List.map(fun x -> x.id,x.parent) |> Map.ofList
        let rec loop acc id = 
            match pmap |> Map.tryFind id with 
            | Some (Some p) -> loop (Set.add p acc) p
            | _             -> acc
        loop Set.empty a.id        

    let rec findMergePair grun attempts = 
        if attempts > 0 then 
            match samplePair grun grun.cfg.max_attempts_find_pair with 
            | Some (a,b) ->
                let pAs = ancestors grun a
                let pBs = ancestors grun b
                if pAs.Contains b.id || pBs.Contains a.id then
                    findMergePair grun (attempts - 1)
                else
                    Some (a,b,Set.intersect pAs pBs)
            | None -> None
        else
            None

    let promptsList (a,b,p) = 
        a.sys.modules 
        |> Map.toList
        |> List.map(fun (k,ma) -> k,(ma.prompt, b.sys.modules.[k].prompt, p.sys.modules.[k].prompt))

    let desirable =
        promptsList >>
        List.exists (fun (_,(aPr,bPr,pPr)) -> (pPr = aPr && aPr <> bPr) || (pPr = bPr && aPr <> bPr))

    let setPrompt k prompt candidate = 
        let m' = {candidate.sys.modules.[k] with prompt = prompt}
        {candidate with sys = {candidate.sys with modules = candidate.sys.modules |> Map.add k m'}}

    let merge grun (a,b,p) =
        let c = {p with id=Utils.newId(); avgScore=0.0}
        let c' =
            (c,promptsList (a,b,p))
            ||> List.fold(fun c (k,(aPr,bPr,pPr)) -> 
                let newPr = 
                    if pPr = aPr && aPr <> bPr then 
                        Some bPr
                    elif pPr = bPr && aPr <> bPr then 
                        Some aPr
                    elif aPr <> bPr && aPr <> pPr && pPr <> bPr then
                        [a.avgScore, aPr; b.avgScore, bPr; p.avgScore, pPr]
                        |> List.groupBy fst
                        |> List.sortByDescending fst
                        |> List.head
                        |> snd
                        |> Utils.randSelect //tie break
                        |> snd
                        |> Some
                    else 
                        None
                newPr 
                |> Option.map(fun pr -> setPrompt k pr c)
                |> Option.defaultValue c
            )
        {grun with candidates = c'::grun.candidates}
        
    let stepMerge grun (a,b,ancestors) = async {
        return 
            (grun,ancestors)
            ||> Seq.fold (fun grun p -> 
                let combo = set [a.id; b.id; p.id]
                if grun.comboSet.Contains combo then grun
                elif p.avgScore > min a.avgScore b.avgScore then grun
                elif not (desirable (a,b,p)) then grun 
                else merge {grun with comboSet = Set.add combo grun.comboSet} (a,b,p)
            )
    }

    let tryForMerge grun = 
        if grun.candidates.Length >= 3 && //need a pair and at least 1 parent
           Utils.rng.NextDouble() > grun.cfg.reflect_merge_split 
        then None
        else findMergePair grun grun.cfg.max_attempts_find_pair

    let run cfg geSys tasksPareto tasksFeedback =
        let grun = {
            count=0
            candidates= [geSys]; 
            cfg=cfg; 
            tasksPareto=tasksPareto; 
            tasksFeedback = tasksFeedback
            comboSet = Set.empty
        }
        let rec loop grun = async {
            if grun.count < cfg.budget then 
                let! grun = 
                    match tryForMerge grun with 
                    | Some pair -> stepMerge grun pair
                    | None -> stepReflective grun
                return! loop {grun with count = grun.count+1}
            else
                return grun
        }
        loop grun
