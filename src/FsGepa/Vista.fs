namespace FsGepa.Run
open FSharp.Control
open FsGepa

module Vista =

    type private ValidatedRewrite<'a,'b> = {
        hypothesis : Hypothesis
        child : GeSystem<'a,'b>
        score : float
        changeSummary : string
    }

    let private processNewBest (runState:GrRun<_,_>) = 
        let newBest = Tlm.postNewBest runState.cfg runState.currentBest runState.candidates
        match newBest with 
        | Some n -> {runState with currentBest = Some n}
        | None -> runState

    let private historyFor runState parentId =
        runState.candidates
        |> List.tryFind (fun c -> c.id = parentId)
        |> Option.map _.history
        |> Option.defaultValue []

    let private finalizeTrace score accepted (trace:OptimizationTraceEntry) =
        {trace with candidateScore = Some score; accepted = Some accepted}

    let private selectHypotheses cfg (hypotheses:Hypothesis list) =
        let desired = max 1 (min cfg.vista.hypotheses_to_validate hypotheses.Length)
        let rec loop (acc:Hypothesis list) (remaining:Hypothesis list) =
            if acc |> List.length >= desired || List.isEmpty remaining then
                List.rev acc
            else
                let ordered = remaining |> List.sortByDescending (fun h -> h.priority, h.confidence)
                let picked =
                    if List.length ordered = 1 then
                        List.head ordered
                    elif Utils.rng.NextDouble() < cfg.vista.epsilon_greedy then
                        Utils.randSelect ordered
                    else
                        List.head ordered
                let remaining =
                    remaining
                    |> List.filter (fun h -> h.id <> picked.id)
                loop (picked::acc) remaining
        loop [] hypotheses

    let private chooseRestartParent (runState:GrRun<'a,'b>) (pool:GrSystem<'a,'b> list) =
        let seedCandidate =
            runState.candidates
            |> List.tryFind (fun c -> c.id = runState.seedId)
            |> Option.defaultValue (Utils.randSelect runState.candidates)

        let frontierCandidate =
            if pool.IsEmpty then seedCandidate else Utils.randSelect pool

        if Utils.rng.NextDouble() <= runState.cfg.vista.restart_from_seed_probability then
            seedCandidate, "seed_restart"
        else
            frontierCandidate, "frontier_restart"

    let private maybeRestart (runState:GrRun<'a,'b>) (pool:GrSystem<'a,'b> list) =
        match runState.cfg.vista.random_restart_stagnation with
        | Some threshold
            when runState.stalled >= threshold
              && Utils.rng.NextDouble() <= runState.cfg.vista.random_restart_probability ->
                let parent,restartReason = chooseRestartParent runState pool
                Tlm.postRestartTriggered runState.cfg parent.id restartReason
                Some (parent,restartReason)
        | _ -> None

    let private tryMerge (prams:ProposePrams<'input,'output>) = async {
        let hasMultiple = prams.pool |> List.tryHead |> Option.map (fun s -> s.sys.modules.Count > 0 ) |> Option.defaultValue false
        if prams.cfg.vista.use_merge && hasMultiple && rng.NextDouble() > prams.cfg.reflect_merge_split then
            return! Merge.tryProposeCandidate prams
        else
            return Merge (None,prams.comboSet)
    }

    let private validateHypothesis prams parent m mEvals parentScore hypothesis = async {
        let! prompt,changeSummary = Llm.Meta.rewritePromptForHypothesis prams.cfg m mEvals hypothesis
        let child = Reflective.setPrompt parent.sys m prompt
        let score = Scoring.averageScoreMultiModel prams.cfg prams.cfg.vista.validation_models child prams.tasksMB
        Tlm.postHypothesisValidated prams.cfg parent.id m.moduleId hypothesis score parentScore
        return {
            hypothesis = hypothesis
            child = child
            score = score
            changeSummary = changeSummary
        }
    }

    let private proposeCandidate (prams:ProposePrams<'input,'output>) (restart:(GrSystem<'input,'output> * string) option) = async {
        let! parent =
            match restart with
            | Some (forcedParent,_) -> async { return forcedParent }
            | None -> Reflective.selectCandidate prams

        let m = Reflective.selectModule prams.cfg parent
        let evals =
            Scoring.score prams.cfg parent.sys prams.tasksMB
            |> AsyncSeq.toBlockingSeq
            |> Seq.toList
        let mEvals =
            let filtered = Reflective.filterEvals m.moduleId evals
            if filtered.IsEmpty then evals else filtered
        let baseParentScore = evals |> List.averageBy (fun x -> x.eval.score)
        let parentScore =
            match prams.cfg.vista.validation_models with
            | [] -> baseParentScore
            | models -> Scoring.averageScoreMultiModel prams.cfg models parent.sys prams.tasksMB
        let! hypotheses = Llm.Meta.generateHypotheses prams.cfg m mEvals
        Tlm.postHypothesesGenerated prams.cfg parent.id m.moduleId hypotheses
        let selectedHypotheses = selectHypotheses prams.cfg hypotheses
        let! validated =
            selectedHypotheses
            |> List.map (validateHypothesis prams parent m mEvals parentScore)
            |> Async.Parallel
        let best =
            validated
            |> Array.toList
            |> List.maxBy (fun candidate -> candidate.score)
        let restarted,restartReason =
            match restart with
            | Some (_,reason) -> true, Some reason
            | None -> false, None
        return {
            candidate = best.child
            parentId = parent.id
            parentMBScore = parentScore
            candidateMBScore = Some best.score
            origin = VistaUpdate (m.moduleId, best.hypothesis.id, best.hypothesis.label, restarted)
            traceEntry = {
                step = prams.step
                action = "vista_hypothesis_update"
                parentId = Some parent.id
                moduleId = Some m.moduleId
                hypothesisId = Some best.hypothesis.id
                hypothesisLabel = Some best.hypothesis.label
                hypothesisSummary = Some best.hypothesis.summary
                evidence = best.hypothesis.evidence
                parentScore = Some parentScore
                candidateScore = Some best.score
                accepted = None
                restartReason = restartReason
                notes = Some best.changeSummary
            }
        }
    }

    let private getProposal (runState:GrRun<'input,'output>) filteredPool tasksMB = async {
        let prams = {step=runState.count; pool=filteredPool; cfg=runState.cfg; tasksMB=tasksMB; comboSet=runState.comboSet}
        match! tryMerge prams with
        | Merge (Some c,cs) -> return c,cs,true
        | Merge (None,cs) ->
            let restart = maybeRestart runState filteredPool
            let! proposal = proposeCandidate prams restart
            return proposal,cs,false
    }

    let rec private loop (runState:GrRun<'input,'output>) = async {
        if runState.count < runState.cfg.budget then 
            Log.info $"run {runState.count}"
            let! filteredPool = Pareto.paretoPool runState
            Tlm.postFrontier runState.cfg filteredPool
            Log.info $"filtered pool {filteredPool.Length}"
            let tasksMB = Scoring.sampleMB runState.cfg runState.tasksFeedback
            let! proposal,comboSet',byMerge = getProposal runState filteredPool tasksMB
            let newScore =
                proposal.candidateMBScore
                |> Option.defaultWith (fun () -> Scoring.averageScore runState.cfg proposal.candidate tasksMB)
            let accepted = newScore > proposal.parentMBScore
            Log.info $"new candidate MB score: {newScore}, parent score: {proposal.parentMBScore}"
            let trace = finalizeTrace newScore accepted proposal.traceEntry
            let runState =
                if accepted then
                    let evals = Scoring.score runState.cfg proposal.candidate runState.tasksPareto |> AsyncSeq.toBlockingSeq |> Seq.toList
                    let parentHistory = historyFor runState proposal.parentId
                    let cRun = {
                        id = newId()
                        sys = proposal.candidate
                        parent = Some proposal.parentId
                        evals = evals
                        origin = proposal.origin
                        history = parentHistory @ [trace]
                    }
                    if byMerge then
                        Tlm.postAddMerge runState.cfg newScore proposal.parentMBScore
                    Tlm.postCandidateAccepted runState.cfg trace
                    {runState with candidates = cRun::runState.candidates; stalled = 0}
                else
                    Tlm.postCandidateRejected runState.cfg trace
                    {runState with stalled = runState.stalled + 1}
            let runState = processNewBest runState
            return! loop {runState with count = runState.count+1; comboSet = comboSet'}
        else
            Log.info $"Reached budget : {runState.cfg.budget}"
            return runState
    }

    let run cfg (geSys:GeSystem<'input,'output>) (tasksPareto:(int*GeTask<'input,'output>) seq) (tasksFeedback:GeTask<'input,'output> seq) = async {
        try 
            Log.info $"Start VISTA: budget: {cfg.budget}, mb:{cfg.mini_batch_size}, pareto:{Seq.length tasksPareto}, hypotheses:{cfg.vista.hypothesis_count}"
            Log.info $"Start initial eval"
            let initEVals = Scoring.score cfg geSys tasksPareto |> AsyncSeq.toBlockingSeq |> Seq.toList
            let seedId = newId()
            let runState = {
                count = 0
                candidates = [{
                    id = seedId
                    sys = geSys
                    parent = None
                    evals = initEVals
                    origin = Seed
                    history = []
                }]
                cfg = cfg
                tasksPareto = tasksPareto
                tasksFeedback = tasksFeedback
                comboSet = Set.empty
                currentBest = None
                seedId = seedId
                stalled = 0
            }
            return! loop runState
        with ex -> 
            Log.exn (ex,"Vista.run")
            return raise ex
    }
