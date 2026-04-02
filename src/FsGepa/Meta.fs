namespace FsGepa.Llm 
open FsGepa

module Meta = 
    open System
    open System.Text.Json
    open System.Text.RegularExpressions

    type MetaResponse = {instructions:string}
    type RewriteResponse = {instructions:string; change_summary:string}

    let truncate len (s:string) = if s.Length < len then s else s.Substring(0,len) + "...[elided]"

    let private withDefaultOpts opts =
        let opts = opts |> Option.defaultValue GenOpts.Default
        Some {opts with max_tokens = opts.max_tokens |> Option.orElse (Some 4096)}

    let rec internal callGenerate attempts (generate:IGenerate) model systemMessage messages responseFormat opts = async {
        try 
            let! resp = generate.generate model systemMessage messages responseFormat (withDefaultOpts opts)
            return resp
        with ex -> 
            if attempts > 0 then 
                Log.warn $"generate call failed. retry {attempts - 1}, error: {ex.Message}"
                do! Async.Sleep 3000
                return! callGenerate (attempts - 1) generate model systemMessage messages responseFormat opts
            else 
                Log.exn (ex,"callGenerate")
                return raise ex
    }

    let private reflectorModel cfg =
        cfg.vista.reflector_model |> Option.defaultValue cfg.default_model

    let private tryDeserialize<'t> (text:string) =
        try
            JsonSerializer.Deserialize<'t>(text, Utils.openAIResponseSerOpts) |> Some
        with _ ->
            None

    let private toKebabCase (s:string) =
        let normalized =
            s.Trim().ToLowerInvariant()
            |> Seq.map (fun c -> if Char.IsLetterOrDigit c then c else '-')
            |> Seq.toArray
            |> String
        Regex.Replace(normalized, "-+", "-").Trim('-')

    let private sanitizeHypothesis index (hypothesis:Hypothesis) =
        let fallbackId = $"hypothesis-{index + 1}"
        let label =
            hypothesis.label
            |> checkEmpty
            |> Option.defaultValue $"Hypothesis {index + 1}"
        let normalizedId =
            hypothesis.id
            |> checkEmpty
            |> Option.defaultValue label
            |> toKebabCase
            |> checkEmpty
            |> Option.defaultValue fallbackId
        let summary =
            hypothesis.summary
            |> checkEmpty
            |> Option.defaultValue label
        {
            id = normalizedId
            label = label
            summary = summary
            suggestedFix =
                hypothesis.suggestedFix
                |> checkEmpty
                |> Option.defaultValue summary
            evidence =
                hypothesis.evidence
                |> List.filter notEmpty
                |> List.distinct
                |> List.truncate 6
            priority = max 1 hypothesis.priority
            confidence = min 1.0 (max 0.0 hypothesis.confidence)
        }

    let private fallbackHypothesis text = 
        [{
            id = "general-rewrite"
            label = "General rewrite"
            summary = text |> shorten 400
            suggestedFix = "Revise the prompt to better match the observed failures."
            evidence = []
            priority = 1
            confidence = 0.25
        }]

    let failureCases (evals:EvaledTask<'a,'b> list) =
        evals |> List.filter (fun evaled -> evaled.eval.score < 0.999999)

    let private feedbackBlock cfg (evals:EvaledTask<'a,'b> list) = 
        evals
        |> List.choose (fun t ->                
            t.flowResult.traces
            |> List.tryHead
            |> Option.map (fun trace ->
                let thinking = 
                    trace.reasoning 
                    |> Option.map(fun x -> $"## ASSISTANT THOUGHTS: {x}") 
                    |> Option.defaultValue ""
                let feedback = 
                    t.eval.feedback.text() 
                    |> checkEmpty
                    |> Option.map (fun x -> $"##EVAL FEEDBACK: {x}") 
                    |> Option.defaultValue ""
                $"""
# -- EXAMPLE START --
## TASK INPUT: {trace.taskInput |> truncate cfg.max_sample_input_prompt_length}
## ASSISTANT RESPONSE : {trace.response}
{feedback}
{thinking}
--- EXAMPLE END ---
"""         ))
        |> String.concat "\n"

    let private traceBlock (trace:OptimizationTraceEntry list) =
        match trace with
        | [] -> "none"
        | xs ->
            xs
            |> List.map (fun entry ->
                let label =
                    entry.hypothesisLabel
                    |> Option.orElse entry.hypothesisId
                    |> Option.defaultValue entry.action
                let delta =
                    match entry.candidateScore, entry.parentScore with
                    | Some candidate, Some parent -> sprintf "%+.4f" (candidate - parent)
                    | _ -> "n/a"
                let notes = entry.notes |> Option.bind checkEmpty |> Option.defaultValue "no notes"
                $"step={entry.step}; label={label}; delta={delta}; notes={notes}")
            |> String.concat "\n"

    let private proposedHypothesesBlock (hypotheses:Hypothesis list) =
        match hypotheses with
        | [] -> "none"
        | xs ->
            xs
            |> List.map (fun h -> $"{h.id}: {h.label}")
            |> String.concat "\n"

    let private renderMetaPrompt cfg modulePrompt metaPromptTemplate (evals:EvaledTask<'a,'b> list) vars additionalInstr =
        let feedback = feedbackBlock cfg evals

        let metaPrompt = 
                ([Vars.current_instruction, modulePrompt :> obj; Vars.input_outputs_feedback, feedback :> obj] @ vars)
                |> Prompts.renderPrompt metaPromptTemplate

        match additionalInstr with 
        | Some addInstr -> $"{metaPrompt}\n\n{addInstr}"
        | None -> metaPrompt

    let generatePrompt<'a,'b> cfg modulePrompt metaPromptTemplate (evals:EvaledTask<'a,'b> list) additionalInstr = async {
        let metaPrompt = renderMetaPrompt cfg modulePrompt metaPromptTemplate evals [] additionalInstr
        let! text = callGenerate 5 cfg.generator cfg.default_model None [{role="user"; content=metaPrompt}] (Some typeof<MetaResponse>) None

        let resp = 
            text.output
            |> tryDeserialize<MetaResponse>
            |> Option.map _.instructions
            |> Option.defaultValue text.output
        FsGepa.Run.Tlm.postGeneratedPrompt cfg resp
        return resp
    }

    let private moduleConstraints (gModule:GeModule) =
        gModule.metaPrompt
        |> Option.map _.metaPrompt
        |> Option.defaultValue ""

    let private requestHypothesis<'a,'b> cfg (gModule:GeModule) (evals:EvaledTask<'a,'b> list) trace priorHypotheses useFree index = async {
        let prompt = 
            renderMetaPrompt cfg gModule.prompt.text (if useFree then Prompts.vistaFreeHypothesisPrompt else Prompts.vistaHeuristicHypothesisPrompt) evals [
                Vars.module_constraints, moduleConstraints gModule :> obj
                Vars.optimization_trace, traceBlock trace :> obj
                Vars.error_taxonomy, Prompts.vistaHeuristicTaxonomy :> obj
                Vars.excluded_hypotheses, proposedHypothesesBlock priorHypotheses :> obj
            ] None

        let! text = callGenerate 5 cfg.generator (reflectorModel cfg) None [{role="user"; content=prompt}] (Some typeof<Hypothesis>) None
        let hypothesis =
            text.output
            |> tryDeserialize<Hypothesis>
            |> Option.defaultValue ((fallbackHypothesis text.output) |> List.head)
            |> sanitizeHypothesis index
        return hypothesis
    }

    let generateHypotheses<'a,'b> cfg (gModule:GeModule) (evals:EvaledTask<'a,'b> list) trace = async {
        let failures = failureCases evals
        if failures.IsEmpty then
            return []
        else
            let desired = max 1 cfg.vista.hypothesis_count
            let rec loop index acc = async {
                if index >= desired then
                    return List.rev acc
                else
                    let useFree = Utils.rng.NextDouble() < cfg.vista.epsilon_greedy
                    let! hypothesis = requestHypothesis cfg gModule failures trace (List.rev acc) useFree index
                    let acc =
                        if acc |> List.exists (fun existing -> existing.id = hypothesis.id || existing.summary = hypothesis.summary) then
                            acc
                        else
                            hypothesis::acc
                    return! loop (index + 1) acc
            }
            let! hypotheses = loop 0 []
            let hypotheses = hypotheses |> List.truncate desired

            return
                if hypotheses.IsEmpty then
                    fallbackHypothesis "No hypotheses generated from the failed minibatch."
                else
                    hypotheses
    }

    let private rewritePromptCore<'a,'b> cfg (gModule:GeModule) (evals:EvaledTask<'a,'b> list) hypothesis additionalInstr = async {
        let prompt = 
            renderMetaPrompt cfg gModule.prompt.text Prompts.vistaRewritePrompt evals [
                Vars.module_constraints, moduleConstraints gModule :> obj
                Vars.selected_label, hypothesis.label :> obj
                Vars.selected_hypothesis, Utils.formatJson hypothesis :> obj
                Vars.selected_fix, hypothesis.suggestedFix :> obj
            ] additionalInstr

        let! text = callGenerate 5 cfg.generator (reflectorModel cfg) None [{role="user"; content=prompt}] (Some typeof<RewriteResponse>) None
        let rewritten, changeSummary =
            match text.output |> tryDeserialize<RewriteResponse> with
            | Some resp ->
                let summary =
                    resp.change_summary
                    |> checkEmpty
                    |> Option.defaultValue hypothesis.summary
                resp.instructions, summary
            | None -> text.output, hypothesis.summary
        return rewritten, changeSummary
    }

    let rewritePromptForHypothesis<'a,'b> cfg (gModule:GeModule) (evals:EvaledTask<'a,'b> list) hypothesis = async {
        let! newPrompt, changeSummary = rewritePromptCore cfg gModule evals hypothesis None
        match gModule.metaPrompt with 
        | Some mMeta ->
            match! mMeta.validate newPrompt with 
            | Some issues -> 
                let extra = $"Revise the prompt to satisfy the validator feedback while still targeting the selected hypothesis.\nValidator feedback:\n{issues}"
                let! revisedPrompt, revisedSummary = rewritePromptCore cfg gModule evals hypothesis (Some extra)
                return {text = revisedPrompt}, $"{changeSummary} {revisedSummary}".Trim()
            | None -> return {text = newPrompt}, changeSummary
        | None -> return {text = newPrompt}, changeSummary
    }

    let private restartPromptCore cfg (gModule:GeModule) (currentDraft:Prompt) observation issue = async {
        let prompt =
            [
                Vars.current_instruction, currentDraft.text :> obj
                Vars.module_constraints, moduleConstraints gModule :> obj
                Vars.restart_observation, observation :> obj
                Vars.restart_issue, (issue |> Option.defaultValue "No explicit issue was recorded.") :> obj
            ]
            |> Prompts.renderPrompt Prompts.vistaRestartPrompt

        let! text = callGenerate 5 cfg.generator (reflectorModel cfg) None [{role="user"; content=prompt}] (Some typeof<RewriteResponse>) None
        let rewritten, changeSummary =
            match text.output |> tryDeserialize<RewriteResponse> with
            | Some resp ->
                let summary =
                    resp.change_summary
                    |> checkEmpty
                    |> Option.defaultValue "Initialized prompt from model output."
                resp.instructions, summary
            | None -> text.output, "Initialized prompt from model output."
        return rewritten, changeSummary
    }

    let restartPromptFromObservation cfg (gModule:GeModule) currentDraft observation issue = async {
        let! newPrompt, changeSummary = restartPromptCore cfg gModule currentDraft observation issue
        match gModule.metaPrompt with
        | Some mMeta ->
            match! mMeta.validate newPrompt with
            | Some issues ->
                let mergedIssue =
                    [ issue |> Option.defaultValue ""; issues ]
                    |> List.filter notEmpty
                    |> String.concat "\n"
                    |> fun x -> if notEmpty x then Some x else None
                let! revisedPrompt, revisedSummary = restartPromptCore cfg gModule currentDraft observation mergedIssue
                return {text = revisedPrompt}, $"{changeSummary} {revisedSummary}".Trim()
            | None -> return {text = newPrompt}, changeSummary
        | None -> return {text = newPrompt}, changeSummary
    }

    let updatePromptOverride<'a,'b> cfg modulePrompt (mMeta:ModuleMetaPrompt) (evals:EvaledTask<'a,'b> list) addInstr =  async {
        let! newPrompt = generatePrompt cfg modulePrompt mMeta.metaPrompt evals addInstr
        match! mMeta.validate newPrompt with 
        | Some issues -> 
            let! revisedPrompt = generatePrompt cfg modulePrompt mMeta.metaPrompt evals (Some $"Taking into account the following additional directions:\n {issues}")
            return {text = revisedPrompt}
        | None -> return {text = newPrompt}
    }

    let updatePrompt<'a,'b> cfg (gModule:GeModule) (evals:EvaledTask<'a,'b> list) =  async {
        let addInstr = gModule.outputSchema |> Option.map (fun x -> $"Output Schema: {x}\n")
        let addInstr = gModule.inputSchema |> Option.map (fun x -> Some $"""Input Schema: {x}\n{addInstr |> Option.defaultValue ""}""" ) |> Option.defaultValue addInstr
    
        match gModule.metaPrompt with 
        | Some mMeta -> return! updatePromptOverride cfg gModule.prompt.text mMeta evals addInstr
        | None -> let! text = generatePrompt cfg gModule.prompt.text Prompts.metaPrompt evals addInstr
                  return {text=text}
    }
