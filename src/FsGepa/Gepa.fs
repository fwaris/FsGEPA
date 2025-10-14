namespace FsGepa
open FSharp.Control
open FsGepa.Run

module Gepa =

    let internal tryMerge (prams:ProposePrams<'input,'output>) = async {
        let hasMultiple = prams.pool |> List.tryHead |> Option.map (fun s -> s.sys.modules.Count > 0 ) |> Option.defaultValue false
        if hasMultiple && rng.NextDouble() > prams.cfg.reflect_merge_split  then 
            return! Merge.tryProposeCandidate prams
        else 
            return Merge (None,prams.comboSet)
    }

    let internal getProposal (prams:ProposePrams<'input,'output>) = async{
        match! tryMerge prams with 
        | Merge (Some c,cs) -> return c,cs,true
        | Merge (None,cs) ->
            let! c = Reflective.proposeCandidate prams
            return c,cs,false
    }

    let internal processNewBest (runState:GrRun<_,_>) = 
        let newBest = Tlm.postNewBest runState.cfg runState.currentBest runState.candidates
        match newBest with 
        | Some n -> {runState with currentBest = Some n}
        | None -> runState


    let rec internal loop (runState:GrRun<_,_>) = async {
        if runState.count < runState.cfg.budget then 
            Log.info $"run {runState.count}"
            let! filteredPool = Pareto.paretoPool runState
            Tlm.postFrontier runState.cfg filteredPool
            Log.info $"filtered pool {filteredPool.Length}"
            let tasksMB = Scoring.sampleMB runState.cfg runState.tasksFeedback
            let proposePrams = {pool=filteredPool; cfg = runState.cfg; tasksMB = tasksMB; comboSet = runState.comboSet}
            let! proposal,comboSet',byMerge = getProposal proposePrams        
            let newScore = Scoring.averageScore runState.cfg proposal.candidate tasksMB
            Log.info $"new candidate MB score: {newScore}, parent score: {proposal.parentMBScore}"
            let runState = 
                if newScore > proposal.parentMBScore then 
                    Tlm.postAdd runState.cfg byMerge newScore proposal.parentMBScore
                    let evals = Scoring.score runState.cfg proposal.candidate runState.tasksPareto |> AsyncSeq.toBlockingSeq |> Seq.toList
                    let cRun = {id=newId(); sys = proposal.candidate; parent=Some proposal.parentId; evals=evals}
                    {runState with candidates = cRun::runState.candidates}
                else
                    runState
            let runState = processNewBest runState
            return! loop {runState with count = runState.count+1; comboSet = comboSet'}
        else
            Log.info $"Reached budget : {runState.cfg.budget}"
            return runState
    }

    let run cfg (geSys:GeSystem<'input,'output>) (tasksPareto:(int*GeTask<'input,'output>) seq) (tasksFeedback:GeTask<'input,'output> seq) = async {
        try 
            Log.info $"Start: budget: {cfg.budget}, mb:{cfg.mini_batch_size}, max attempts: ({cfg.max_attempts_find_pair}, {cfg.max_attempts_find_merge_parent}), pareto:{Seq.length tasksPareto}"
            Log.info $"Start initial eval"
            let initEVals = Scoring.score cfg geSys tasksPareto |> AsyncSeq.toBlockingSeq |> Seq.toList
            let runState = {
                count=0
                candidates= [{id=newId(); sys=geSys; parent=None; evals=initEVals}]; 
                cfg=cfg; 
                tasksPareto=tasksPareto; 
                tasksFeedback = tasksFeedback
                comboSet = Set.empty            
                currentBest = None
            }
            return! loop runState
        with ex -> 
            Log.exn (ex,"run")
            return raise ex
    }

