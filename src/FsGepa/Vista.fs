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
        hypotheses
        |> List.sortByDescending (fun h -> h.priority, h.confidence)
        |> List.truncate (max 1 (min cfg.vista.hypotheses_to_validate hypotheses.Length))

    let private shouldRestart (runState:GrRun<'a,'b>) =
        let gateOpen =
            match runState.cfg.vista.random_restart_stagnation with
            | Some threshold -> runState.stalled >= threshold
            | None -> true
        gateOpen && Utils.rng.NextDouble() <= runState.cfg.vista.random_restart_probability

    let private tryMerge (prams:ProposePrams<'input,'output>) = async {
        let hasMultiple = prams.pool |> List.tryHead |> Option.map (fun s -> s.sys.modules.Count > 0 ) |> Option.defaultValue false
        if prams.cfg.vista.use_merge && hasMultiple && rng.NextDouble() > prams.cfg.reflect_merge_split then
            return! Merge.tryProposeCandidate prams
        else
            return Merge (None,prams.comboSet)
    }

    let private parentScoreFor prams parent evals =
        let baseParentScore = evals |> List.averageBy (fun x -> x.eval.score)
        match prams.cfg.vista.validation_models with
        | [] -> baseParentScore
        | models -> Scoring.averageScoreMultiModel prams.cfg models parent.sys prams.tasksMB

    let private parentModuleEvals prams parent m =
        let evals =
            Scoring.score prams.cfg parent.sys prams.tasksMB
            |> AsyncSeq.toBlockingSeq
            |> Seq.toList
        let mEvals =
            let filtered = Reflective.filterEvals m.moduleId evals
            if filtered.IsEmpty then evals else filtered
        evals, mEvals

    let private observeModuleRun cfg sys (m:GeModule) (taskIndex,task) = async {
        let! _,task,flowResult = Scoring.runTask 5 cfg sys (taskIndex,task)
        let! evaled = Scoring.evalTask cfg (taskIndex,task,flowResult)
        let observation =
            flowResult.traces
            |> List.tryFind (fun trace -> trace.moduleId = m.moduleId)
            |> Option.orElse (flowResult.traces |> List.tryHead)
            |> Option.map _.response
            |> Option.defaultValue "No module trace was captured."
        let issue =
            if evaled.eval.score >= 0.999999 then
                None
            else
                evaled.eval.feedback.text() |> checkEmpty
        return evaled,observation,issue
    }

    let private restartLookahead prams parent m seedEval = async {
        let maxSteps = max 1 prams.cfg.vista.restart_lookahead_steps
        let rec loop step currentDraft summaries = async {
            let sys = Reflective.setPrompt parent.sys m currentDraft
            let! _,observation,issue = observeModuleRun prams.cfg sys m (seedEval.index, seedEval.task)
            let! nextPrompt,summary = Llm.Meta.restartPromptFromObservation prams.cfg m currentDraft observation issue
            let summaries = summaries @ [summary]
            if step >= maxSteps || issue.IsNone then
                return nextPrompt,summaries
            else
                return! loop (step + 1) nextPrompt summaries
        }
        return! loop 1 {text=""} []
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

    let private validateHypothesisSafe prams parent m mEvals parentScore hypothesis = async {
        let! result = validateHypothesis prams parent m mEvals parentScore hypothesis |> Async.Catch
        match result with
        | Choice1Of2 validated -> return Some validated
        | Choice2Of2 ex ->
            Log.warn $"VISTA hypothesis validation failed for module={m.moduleId}, hypothesis={hypothesis.id}/{hypothesis.label}. Error: {ex.Message}"
            return None
    }

    let private proposeRestartCandidate (prams:ProposePrams<'input,'output>) parent = async {
        let m = Reflective.selectModule prams.cfg parent
        let evals,mEvals = parentModuleEvals prams parent m
        let parentScore = parentScoreFor prams parent evals
        let seedEval =
            Llm.Meta.failureCases mEvals
            |> List.tryHead
            |> Option.orElse (mEvals |> List.tryHead)
            |> Option.orElse (evals |> List.tryHead)
        match seedEval with
        | Some seedEval ->
            Tlm.postRestartTriggered prams.cfg parent.id "blank_restart"
            let! prompt,changeSummaries = restartLookahead prams parent m seedEval
            let child = Reflective.setPrompt parent.sys m prompt
            let score = Scoring.averageScoreMultiModel prams.cfg prams.cfg.vista.validation_models child prams.tasksMB
            let notes =
                changeSummaries
                |> List.filter notEmpty
                |> String.concat " "
                |> checkEmpty
            return {
                candidate = child
                parentId = parent.id
                parentMBScore = parentScore
                candidateMBScore = Some score
                origin = VistaUpdate (m.moduleId, "blank-restart", "initialize_from_model_output", true)
                traceEntry = {
                    step = prams.step
                    action = "vista_restart"
                    parentId = Some parent.id
                    moduleId = Some m.moduleId
                    hypothesisId = Some "blank-restart"
                    hypothesisLabel = Some "initialize_from_model_output"
                    hypothesisSummary = Some "Initialize the prompt from model output rather than inherited seed text."
                    evidence =
                        [
                            seedEval.eval.feedback.text()
                        ]
                        |> List.filter notEmpty
                    parentScore = Some parentScore
                    candidateScore = Some score
                    accepted = None
                    restartReason = Some "blank_restart"
                    notes = notes
                }
            }
        | None ->
            Log.warn $"VISTA restart fallback to reflective update for module={m.moduleId}; no seed task was available."
            return! Reflective.proposeCandidateForParent prams parent
    }

    let private proposeHypothesisCandidate (prams:ProposePrams<'input,'output>) parent = async {
        let m = Reflective.selectModule prams.cfg parent
        let evals,mEvals = parentModuleEvals prams parent m
        let failures = Llm.Meta.failureCases mEvals
        if failures.IsEmpty then
            Log.warn $"VISTA fallback to reflective update for module={m.moduleId}; minibatch had no failed cases."
            return! Reflective.proposeCandidateForParent prams parent
        else
            let parentScore = parentScoreFor prams parent evals
            let! hypotheses = Llm.Meta.generateHypotheses prams.cfg m failures parent.history
            Tlm.postHypothesesGenerated prams.cfg parent.id m.moduleId hypotheses
            let selectedHypotheses = selectHypotheses prams.cfg hypotheses
            let! validated =
                selectedHypotheses
                |> List.map (validateHypothesisSafe prams parent m failures parentScore)
                |> Async.Parallel
            let validated =
                validated
                |> Array.choose id
                |> Array.toList
            match validated with
            | _::_ ->
                let best = validated |> List.maxBy (fun candidate -> candidate.score)
                return {
                    candidate = best.child
                    parentId = parent.id
                    parentMBScore = parentScore
                    candidateMBScore = Some best.score
                    origin = VistaUpdate (m.moduleId, best.hypothesis.id, best.hypothesis.label, false)
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
                        restartReason = None
                        notes = Some best.changeSummary
                    }
                }
            | [] ->
                Log.warn $"VISTA fallback to reflective update for module={m.moduleId}; no hypotheses validated successfully."
                return! Reflective.proposeCandidateForParent prams parent
    }

    let private getProposal (runState:GrRun<'input,'output>) filteredPool tasksMB = async {
        let prams = {step=runState.count; pool=filteredPool; cfg=runState.cfg; tasksMB=tasksMB; comboSet=runState.comboSet}
        match! tryMerge prams with
        | Merge (Some c,cs) -> return c,cs,true
        | Merge (None,cs) ->
            let! parent = Reflective.selectCandidate prams
            let! proposal =
                if shouldRestart runState then
                    proposeRestartCandidate prams parent
                else
                    proposeHypothesisCandidate prams parent
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

    let run cfg (geSys:GeSystem<'input,'output>) (tasksPareto:(int*GeTask<'input,'output>) seq) (tasksFeedback:GeTask<'input,'output> seq) =
        ScheduledRun.withScheduledGenerator cfg (fun cfg -> async {
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
        })
