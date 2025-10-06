namespace FsGepa.Run
open FSharp.Control
open FsGepa

module Reflective = 
    
    let filterEval mid (evaledTask:EvaledTask<_,_>) = 
        let traces = evaledTask.eval.traces |> List.filter (fun t -> t.moduleId = mid)
        {evaledTask with eval.traces=traces}

    let filterEvals cfg moduleId = List.map (filterEval moduleId)

    let setPrompt sys m prompt = 
        {sys with 
            modules = 
                sys.modules 
                |> Map.map(fun k v -> 
                    if k = m.moduleId then {v with prompt=prompt} else v)  
        }

    let selectCandidate (parms:ProposeParms<_,_>) = async {
        let scores = parms.pool |> List.map (fun c -> c, c.avgScore)
        let total = scores |> List.sumBy snd
        let _,cums = ((0.0,[]),scores) ||> List.fold (fun (cum,acc) (c,scr) -> let cum = cum + (scr/total) in cum,(c,cum)::acc)
        let dice = Utils.rng.NextDouble()
        let selectedC = cums |> List.skipWhile (fun (c,cum) -> cum < dice) |> List.head
        return fst selectedC
    }

    let selectModule cfg grSys = grSys.sys.modules |> Map.toList |> List.map snd |> Utils.randSelect 

    ///single step of reflective prompt optimizer process
    let proposeCandidate (parms:ProposeParms<_,_>) : Async<ProposedCandidate<_,_>> = async {
        let! candidate = selectCandidate parms
        let m = selectModule parms.cfg candidate
        let evals = Run.score parms.cfg candidate.sys parms.tasksMB
                    |> AsyncSeq.toBlockingSeq
                    |> Seq.toList
        let mEvals = filterEvals parms m.moduleId evals
        let! prompt = Llm.Llm.updatePrompt parms.cfg m.prompt mEvals
        let child = setPrompt candidate.sys m prompt
        let proposed = {
                            candidate = child
                            parentId = candidate.id
                            parentMBScore = evals |> List.averageBy (fun x->x.eval.score)
                        }
        return proposed              
    }
