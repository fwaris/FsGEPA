namespace FsGepa.Run

module Merge = 
    let rec samplePair prams attempts = 
        if attempts > 0 then 
            let a = prams.pool |> FsGepa.Utils.randSelect
            let b = prams.pool |> FsGepa.Utils.randSelect
            if a.id <> b.id then 
                Some (a,b)
            else
                samplePair prams (attempts - 1)
        else
            None

    let ancestors poolMap (a:GrSystem<'a,'b>) =
        let rec loop acc id = 
            match poolMap |> Map.tryFind id with 
            | Some p -> match p.parent with 
                        | Some pid -> loop (Set.add pid acc) pid
                        | None -> acc
            | None -> acc
        loop Set.empty a.id      

    let newComboPossible comboSet (a,b,ancestors)  =
        ancestors
        |> Set.exists (fun p -> Set.contains (set [a;b;p]) comboSet |> not)

    let promptsList (a,b,p) = 
        a.sys.modules 
        |> Map.toList
        |> List.map(fun (k,ma) -> k,(ma.prompt, b.sys.modules.[k].prompt, p.sys.modules.[k].prompt))

    let desirable (a,b,p)=
        promptsList (a,b,p)
        |> List.exists (fun (_,(aPr,bPr,pPr)) -> (pPr = aPr && aPr <> bPr) || (pPr = bPr && aPr <> bPr))

    let isDesirable poolMap (a,b) pid = 
        let p = poolMap |> Map.find pid
        desirable(a,b,p)

    let rec findMergePair prams poolMap attempts = 
        if attempts > 0 then 
            match samplePair prams prams.cfg.max_attempts_find_pair with 
            | Some (a,b) ->
                let pAs = ancestors poolMap a
                let pBs = ancestors poolMap b
                if pAs.Contains b.id || pBs.Contains a.id then
                    findMergePair prams poolMap (attempts - 1)
                else
                    let commonParents = Set.intersect pAs pBs
                    let combos = commonParents |> Set.map (fun p -> set [a.id;b.id;p])                     
                    let untriedCombos = Set.difference combos prams.comboSet
                    let untriedParents = untriedCombos |> Set.map(fun xs -> xs |> Set.filter (fun i -> i <> a.id && i <> b.id) |> Seq.head)
                    let desirable = untriedParents |> Set.filter (isDesirable poolMap (a,b))
                    if desirable.IsEmpty then 
                        findMergePair prams poolMap (attempts - 1)
                    else 
                        Some(a,b,desirable)
            | None -> None
        else
            None

    let setPrompt k prompt (sys:FsGepa.GeSystem<_,_>) = 
        let m' = {sys.modules.[k] with prompt = prompt}
        {sys with modules = sys.modules |> Map.add k m'}

    let merge (a,b,p) =
        let updatedPrompts = 
            promptsList (a,b,p)
            |> List.choose(fun (k,(aPr,bPr,pPr)) -> 
                if pPr = aPr && aPr <> bPr then 
                    Some (k,bPr)
                elif pPr = bPr && aPr <> bPr then 
                    Some (k,aPr)
                elif aPr <> bPr && aPr <> pPr && pPr <> bPr then
                    let pr = 
                        [a.avgScore.Value, aPr; b.avgScore.Value, bPr; p.avgScore.Value, pPr]
                        |> List.groupBy fst
                        |> List.sortByDescending fst
                        |> List.head
                        |> snd
                        |> FsGepa.Utils.randSelect //tie break
                        |> snd
                    Some (k,pr)
                else 
                    None)
        if updatedPrompts.IsEmpty then 
            None // try next combo
        else 
            (p.sys,updatedPrompts)
            ||> List.fold(fun c (k,pr) -> setPrompt k pr c)
            |> Some
        

    let tryMergeFromParent (combos:Set<Set<string>>) (a,b,p) = 
        let combo = set [a.id; b.id; p.id]
        let combos = Set.add combo combos
        let proposed = merge (a,b,p)
        combos, proposed |> Option.map(fun c -> {candidate=c; parentId=p.id; parentMBScore = p.avgScore.Value})
        
    let tryProposeFromPair prams (a,b,ancestors) =
        let refSet = ref prams.comboSet
        ((prams.comboSet,None),ancestors)
        ||> Seq.scan (fun (combos,_) ancestor -> tryMergeFromParent combos (a,b,ancestor))
        |> Seq.skipWhile (fun (combos,n) -> refSet.Value <- combos; n.IsNone)
        |> Seq.tryHead
        |> Option.map(fun (_,n) -> Merge (n, refSet.Value))
        |> Option.defaultValue (Merge (None,refSet.Value))

    let tryProposeCandidate (prams:ProposePrams<_,_>) : Async<MergeProposal<_,_>> = 
        let proposal =
            if prams.pool.Length <= 3 then 
                Merge(None,prams.comboSet)
            else 
                let poolMap = prams.pool |> List.map(fun x -> x.id,x) |> Map.ofList
                match findMergePair prams poolMap prams.cfg.max_attempts_find_merge_parent with 
                | Some (a,b,ancestorIds) -> tryProposeFromPair prams (a,b,ancestorIds |> Set.toList |> List.map(fun id -> poolMap.[id]))
                | None -> Merge (None,prams.comboSet)
        async{return proposal}
