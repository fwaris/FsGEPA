namespace FsGepa.Run

module Merge = 
    let rec samplePair parms attempts = 
        if attempts > 0 then 
            let a = parms.pool |> FsGepa.Utils.randSelect
            let b = parms.pool |> FsGepa.Utils.randSelect
            if a.id <> b.id then 
                Some (a,b)
            else
                samplePair parms (attempts - 1)
        else
            None

    let ancestors poolMap (a:GrSystem<'a,'b>) =
        let rec loop acc id = 
            match (poolMap |> Map.find id).parent with 
            | Some pid -> loop (Set.add pid acc) pid
            | _        -> acc
        loop Set.empty a.id      

    let newComboPossible comboSet (a,b,ancestors)  =
        ancestors
        |> Set.exists (fun p -> Set.contains (set [a;b;p]) comboSet |> not)

    let rec findMergePair parms poolMap attempts = 
        if attempts > 0 then 
            match samplePair parms parms.cfg.max_attempts_find_pair with 
            | Some (a,b) ->
                let pAs = ancestors poolMap a
                let pBs = ancestors poolMap b
                if pAs.Contains b.id || pBs.Contains a.id then
                    findMergePair parms poolMap (attempts - 1)
                else
                    let commonParents = Set.intersect pAs pBs 
                    if commonParents.IsEmpty then 
                        findMergePair parms poolMap (attempts - 1)
                    elif not (newComboPossible parms.comboSet (a.id,b.id,commonParents)) then 
                         findMergePair parms poolMap (attempts - 1)
                    else
                        Some (a,b,commonParents)
            | None -> None
        else
            None

    let promptsList (a,b,p) = 
        a.sys.modules 
        |> Map.toList
        |> List.map(fun (k,ma) -> k,(ma.prompt, b.sys.modules.[k].prompt, p.sys.modules.[k].prompt))

    let desirable (a,b,p)=
        promptsList (a,b,p)
        |> List.exists (fun (_,(aPr,bPr,pPr)) -> (pPr = aPr && aPr <> bPr) || (pPr = bPr && aPr <> bPr))

    let setPrompt k prompt candidate = 
        let m' = {candidate.sys.modules.[k] with prompt = prompt}
        {candidate with sys = {candidate.sys with modules = candidate.sys.modules |> Map.add k m'}}

    let merge parms (a,b,p) =
        let c = {p with id=FsGepa.Utils.newId()}
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
                        |> FsGepa.Utils.randSelect //tie break
                        |> snd
                        |> Some
                    else 
                        None
                newPr 
                |> Option.map(fun pr -> setPrompt k pr c)
                |> Option.defaultValue c
            )
        {parms with candidates = c'::parms.candidates}
        
    let tryProposeFromPair parms (a,b,ancestors) = 
        ((parms.comboSet,None),ancestors)
        |> Seq.scan (fun (combos,_) ancestor -> tryMerge combos (a,b,ancestor))
        |> Seq.choose snd 
        |> Seq.tryHead


        return 
            (parms,ancestors)
            ||> Seq.fold (fun parms p -> 
                let combo = set [a.id; b.id; p.id]
                if parms.comboSet.Contains combo then parms
                elif p.avgScore > min a.avgScore b.avgScore then parms
                elif not (desirable (a,b,p)) then parms 
                else merge {parms with comboSet = Set.add combo parms.comboSet} (a,b,p)
            )
    

    let tryProposeCandidate (parms:ProposeParms<_,_>) : Async<MergeProposal<_,_>> = 
        let proposal =
            if parms.pool.Length >= 3 then 
                Merge(None,parms.comboSet)
            else 
                let poolMap = parms.pool |> List.map(fun x -> x.id,x) |> Map.ofList
                match findMergePair parms poolMap parms.cfg.max_attempts_merge_pair with 
                | Some (a,b,ancestors) -> tryProposeFromPair parms (a,b,ancestors)
                | None -> Merge (None,parms.comboSet)
        async{return proposal}
