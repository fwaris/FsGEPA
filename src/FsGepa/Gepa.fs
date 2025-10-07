namespace FsGepa
open FSharp.Control
open FsGepa.Run

module Gepa =

    let internal tryMerge (parms:ProposeParms<_,_>) = async {
        if rng.NextDouble() > parms.cfg.reflect_merge_split  then 
            return! Merge.tryProposeCandidate parms
        else 
            return Merge (None,parms.comboSet)
    }

    let internal getProposal parms = async{
        match! tryMerge parms with 
        | Merge (Some c,cs) -> return c,cs
        | Merge (None,cs) ->
            let! c = Reflective.proposeCandidate parms
            return c,cs
    }

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
            if grun.count < grun.cfg.budget then 
                let! filteredPool = Pareto.paretoPool grun
                let tasksMB = Run.sampleMB grun.cfg grun.tasksFeedback
                let proposeParms = {pool=filteredPool; cfg = grun.cfg; tasksMB = tasksMB; comboSet = grun.comboSet}
                let! proposal,comboSet' = getProposal proposeParms
                let newScore = Run.averageScore grun.cfg proposal.candidate tasksMB
                let grun = 
                    if newScore > proposal.parentMBScore then 
                        let evals = Run.score grun.cfg proposal.candidate grun.tasksPareto |> AsyncSeq.toBlockingSeq |> Seq.toList
                        let cRun = {id=newId(); sys = proposal.candidate; parent=Some proposal.parentId; evals=evals}
                        {grun with candidates = cRun::grun.candidates}
                    else
                        grun
                return! loop {grun with count = grun.count+1; comboSet = comboSet'}
            else
                return grun
        }
        loop grun
