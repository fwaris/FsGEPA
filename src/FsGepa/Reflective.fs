namespace FsGepa.Run
open FSharp.Control
open FsGepa

module Reflective = 
    
    ///Filter traces to that of a specific module
    let filterEval mid (evaledTask:EvaledTask<'a,'b>) = 
        let traces = evaledTask.flowResult.traces |> List.filter (fun t -> t.moduleId = mid)
        {evaledTask with flowResult = {evaledTask.flowResult with traces = traces}}

    let filterEvals moduleId =
        List.map (filterEval moduleId)
        >> List.filter (fun e -> e.flowResult.traces |> List.isEmpty |> not)

    let setPrompt sys m prompt = 
        {sys with 
            modules = 
                sys.modules 
                |> Map.map(fun k v -> 
                    if k = m.moduleId then {v with prompt=prompt} else v)  
        }

    let selectCandidate (prams:ProposePrams<_,_>) = async {
        let scores = prams.pool |> List.map (fun c -> c, c.avgScore.Value)
        let total = scores |> List.sumBy snd
        if total <= 0.0 then
            return Utils.randSelect prams.pool
        else
            let _,cums = ((0.0,[]),scores) ||> List.fold (fun (cum,acc) (c,scr) -> let cum = cum + (scr/total) in cum,(c,cum)::acc)
            let dice = Utils.rng.NextDouble()
            let selectedC = cums |> List.skipWhile (fun (_,cum) -> cum < dice) |> List.head
            return fst selectedC
    }

    let selectModule cfg grSys = grSys.sys.modules |> Map.toList |> List.map snd |> Utils.randSelect 

    ///single step of reflective prompt optimizer process
    let proposeCandidate (prams:ProposePrams<'input,'output>) = async {
        let! candidate = selectCandidate prams
        let m = selectModule prams.cfg candidate
        let evals = Scoring.score prams.cfg candidate.sys prams.tasksMB
                    |> AsyncSeq.toBlockingSeq
                    |> Seq.toList
        let mEvals =
            let filtered = filterEvals m.moduleId evals
            if filtered.IsEmpty then evals else filtered
        let parentScore = evals |> List.averageBy (fun x -> x.eval.score)
        let! prompt = Llm.Meta.updatePrompt prams.cfg m mEvals
        let child = setPrompt candidate.sys m prompt
        let proposed = {
                            candidate = child
                            parentId = candidate.id
                            parentMBScore = parentScore
                            candidateMBScore = None
                            origin = ReflectiveUpdate m.moduleId
                            traceEntry = {
                                step = prams.step
                                action = "reflective_update"
                                parentId = Some candidate.id
                                moduleId = Some m.moduleId
                                hypothesisId = None
                                hypothesisLabel = None
                                hypothesisSummary = None
                                evidence = []
                                parentScore = Some parentScore
                                candidateScore = None
                                accepted = None
                                restartReason = None
                                notes = None
                            }
                        }
        return proposed              
    }
