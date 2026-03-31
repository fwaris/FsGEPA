namespace FsgSample.Gsm8k
open System
open System.Text.Json
open System.Text.RegularExpressions
open FSharp.Control
open FsGepa

type SeedKind =
    | Defective
    | Minimal
    with
        static member parse(value:string option) =
            match value |> Option.map _.Trim().ToLowerInvariant() with
            | Some "minimal" -> Minimal
            | _ -> Defective

type CompareMode =
    | Both
    | GepaOnly
    | VistaOnly
    with
        static member parse(value:string option) =
            match value |> Option.map _.Trim().ToLowerInvariant() with
            | Some "gepa" -> GepaOnly
            | Some "vista" -> VistaOnly
            | _ -> Both

module Opt =
    let private envOrDefault name fallback =
        Environment.GetEnvironmentVariable(name)
        |> checkEmpty
        |> Option.defaultValue fallback

    let private envOrDefaultInt name fallback =
        Environment.GetEnvironmentVariable(name)
        |> checkEmpty
        |> Option.bind (fun value -> match Int32.TryParse value with | true,n -> Some n | _ -> None)
        |> Option.defaultValue fallback

    let private envOrDefaultFloat name fallback =
        Environment.GetEnvironmentVariable(name)
        |> checkEmpty
        |> Option.bind (fun value ->
            match Double.TryParse(value, Globalization.NumberStyles.Float ||| Globalization.NumberStyles.AllowThousands, Globalization.CultureInfo.InvariantCulture) with
            | true,n -> Some n
            | _ -> None)
        |> Option.defaultValue fallback

    let private llamaCppEndpoint = envOrDefault "FSGEPA_LLAMACPP_ENDPOINT" "http://localhost:8081/v1"
    let private llamaCppModel = envOrDefault "FSGEPA_LLAMACPP_MODEL" "gpt-oss-20b-mxfp4.gguf"

    let private backendLlamaCppGptOss : FsGepa.GenAI.Backend =
        {
            endpoint = {
                API_KEY = "cannot be empty"
                ENDPOINT = llamaCppEndpoint
            }
            backendType = FsGepa.GenAI.BackendType.ChatCompletionsHarmony
        }

    let private defectiveSeedText =
        """You are an AI assistant that solves mathematical word problems. You will be given a question and you need to provide a step-by-step solution to the problem. Finally, you will provide the answer to the question. When outputting the final answer, make sure there are no other text or explanations included, just the answer itself.

The expected output must be a JSON object with the following format:
{
  "final_answer": <the final answer to the question>,
  "solution_pad": <the step-by-step solution to the problem>
}

Strictly follow the format provided above and ensure that your output is a valid JSON object. Any deviation from this format will result in an error."""

    let private minimalSeedText =
        """Solve and output in a single json:
{
  "final_answer": <answer>
}"""

    let private seedPrompt = function
        | Defective -> defectiveSeedText
        | Minimal -> minimalSeedText

    let private solverModule seed =
        { GeModule.Default with
            prompt = {text = seedPrompt seed}
            moduleId = "solve"
        }

    type SolveRequest = {question:string}

    type RawAnswer = {
        final_answer : string
        solution_pad : string option
    }

    type ComparisonResult = {
        name : string
        baseline : float
        optimized : float
        improvement : float
        elapsed : TimeSpan
        candidateCount : int
    }

    let private stripHarmonyEnvelope (text:string) =
        let afterEnd =
            match text.LastIndexOf("<|end|>", StringComparison.Ordinal) with
            | i when i >= 0 -> text.Substring(i + "<|end|>".Length)
            | _ -> text
        let afterFinal =
            afterEnd.Replace("<|channel|>final<|message|>", "", StringComparison.Ordinal)
        match afterFinal.Trim() with
        | "" when text.Contains("<|message|>", StringComparison.Ordinal) ->
            let idx = text.LastIndexOf("<|message|>", StringComparison.Ordinal)
            text.Substring(idx + "<|message|>".Length).Trim()
        | cleaned -> cleaned

    let private tryExtractJsonObject (text:string) =
        let startIdx = text.IndexOf('{')
        let endIdx = text.LastIndexOf('}')
        if startIdx >= 0 && endIdx > startIdx then
            Some (text.Substring(startIdx, endIdx - startIdx + 1))
        else
            None

    let private tryStructuredAnswer (candidate:string) =
        try
            JsonSerializer.Deserialize<RawAnswer>(candidate, Utils.openAIResponseSerOpts)
            |> Some
        with _ ->
            None

    let private tryReadJsonAnswer (candidate:string) : Answer option =
        try
            use doc = JsonDocument.Parse(candidate)
            let root = doc.RootElement
            let mutable finalAnswerProp = Unchecked.defaultof<JsonElement>
            if root.ValueKind = JsonValueKind.Object && root.TryGetProperty("final_answer", &finalAnswerProp) then
                let finalAnswer =
                    match finalAnswerProp.ValueKind with
                    | JsonValueKind.String -> finalAnswerProp.GetString()
                    | _ -> finalAnswerProp.GetRawText().Trim('"')
                let solutionPad =
                    let mutable prop = Unchecked.defaultof<JsonElement>
                    if root.TryGetProperty("solution_pad", &prop) then
                        match prop.ValueKind with
                        | JsonValueKind.String -> Some (prop.GetString())
                        | JsonValueKind.Null -> None
                        | _ -> Some (prop.GetRawText().Trim('"'))
                    else
                        None
                if notEmpty finalAnswer then
                    Some {
                        final_answer = finalAnswer
                        solution_pad = solutionPad
                    }
                else
                    None
            else
                None
        with _ ->
            None

    let private finalAnswerRegex =
        Regex("\"final_answer\"\\s*:\\s*(\"(?<quoted>(?:\\\\.|[^\"])*)\"|(?<bare>[-+]?\\d[\\d,]*(?:\\.\\d+)?))", RegexOptions.Compiled ||| RegexOptions.IgnoreCase ||| RegexOptions.Singleline)

    let private tryRegexAnswer (candidate:string) : Answer option =
        let m = finalAnswerRegex.Match(candidate)
        if m.Success then
            let value =
                if m.Groups.["quoted"].Success then m.Groups.["quoted"].Value
                elif m.Groups.["bare"].Success then m.Groups.["bare"].Value
                else ""
            if notEmpty value then
                Some { final_answer = value; solution_pad = None }
            else
                None
        else
            None

    let private extractAnswerFallback (candidate:string) =
        let lines =
            candidate.Split([|'\n'; '\r'|], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter notEmpty
        match lines |> Array.tryLast with
        | Some last -> last.Trim('"', ' ', '`')
        | None -> candidate.Trim()

    let private parseAnswerOutput raw : Answer =
        let cleaned = stripHarmonyEnvelope raw
        let candidate = tryExtractJsonObject cleaned |> Option.defaultValue cleaned
        match tryStructuredAnswer candidate with
        | Some parsed when notEmpty parsed.final_answer ->
            {
                final_answer = parsed.final_answer
                solution_pad = parsed.solution_pad
            }
        | _ ->
            match tryReadJsonAnswer candidate with
            | Some parsed -> parsed
            | None ->
                match tryRegexAnswer candidate with
                | Some parsed -> parsed
                | None ->
                    {
                        final_answer = extractAnswerFallback candidate
                        solution_pad = None
                    }

    let flow cfg (modules:Map<string,GeModule>) (input:Gsm8kInput) : Async<FlowResult<Answer>> = async {
        let solve = modules.["solve"]
        let model = solve.model |> Option.defaultValue cfg.default_model
        let taskInput = JsonSerializer.Serialize({question = input.question}, Utils.openAIResponseSerOpts)
        let! resp =
            cfg.generator.generate
                model
                (Some solve.prompt.text)
                [{role = "user"; content = taskInput}]
                None
                (Some {GenOpts.Default with temperature = Some 0.2f; max_tokens = Some 512})
        let answerSource =
            if notEmpty resp.output then resp.output
            else resp.thoughts |> Option.defaultValue ""
        let answer = parseAnswerOutput answerSource
        return {
            output = answer
            traces = [{
                moduleId = solve.moduleId
                taskInput = taskInput
                response = answerSource
                reasoning = resp.thoughts
            }]
        }
    }

    let createInitialCandidate seed =
        let solve = solverModule seed
        {
            modules = [solve.moduleId, solve] |> Map.ofList
            flow = flow
        }

    let channel = System.Threading.Channels.Channel.CreateBounded<Telemetry>(10)

    channel.Reader.ReadAllAsync()
    |> AsyncSeq.ofAsyncEnum
    |> AsyncSeq.iter(printfn "%A")
    |> Async.Start

    let private configBase feedbackSize =
        let generate = FsGepa.GenAI.Api.createDefault backendLlamaCppGptOss
        let flowParallel = envOrDefaultInt "FSGEPA_FLOW_PARALLELISM" 5
        { Config.CreateDefault generate feedbackSize {id = llamaCppModel} with
            mini_batch_size = 8
            // Keep concurrency conservative by default; allow env override for tuning
            flow_parallelism = flowParallel
        }

    let private quietConfig budget miniBatch feedbackSize mode =
        let baseCfg =
            match mode with
            | GepaMode -> configBase feedbackSize
            | VistaMode ->
                { configBase feedbackSize with
                    optimizer_mode = VistaMode
                    vista = {
                        VistaConfig.Default with
                            hypothesis_count = 3
                            hypotheses_to_validate = 3
                            epsilon_greedy = 0.10
                            random_restart_stagnation = None
                            random_restart_probability = 0.20
                    }
                }
        {
            baseCfg with
                budget = budget
                mini_batch_size = miniBatch
                telemetry_channel = None
        }

    let private runExperiment name cfg baseline sys pareto feedback testSet = async {
        let ts0 = DateTime.UtcNow
        let! finalRunState = Gepa.run cfg sys pareto feedback
        let elapsed = DateTime.UtcNow - ts0
        let sysStar = finalRunState.candidates |> List.maxBy _.avgScore.Value
        let optimizedScore = FsGepa.Run.Scoring.averageScore cfg sysStar.sys testSet
        let result = {
            name = name
            baseline = baseline
            optimized = optimizedScore
            improvement = optimizedScore - baseline
            elapsed = elapsed
            candidateCount = finalRunState.candidates.Length
        }
        printfn $"{result.name}: baseline=%0.4f{result.baseline}; optimized=%0.4f{result.optimized}; improvement=%+0.4f{result.improvement}; candidates={result.candidateCount}; elapsed={result.elapsed}"
        return result
    }

    let compare seed mode = async {
        let budget = envOrDefaultInt "FSGEPA_GSM8K_COMPARE_BUDGET" 8
        let miniBatch = envOrDefaultInt "FSGEPA_GSM8K_COMPARE_MINI_BATCH" 8
        let paretoCount = envOrDefaultInt "FSGEPA_GSM8K_COMPARE_PARETO" 50
        let feedbackCount = envOrDefaultInt "FSGEPA_GSM8K_COMPARE_FEEDBACK" 50
        let holdoutCount = envOrDefaultInt "FSGEPA_GSM8K_COMPARE_HOLDOUT" 200
        let allPareto, allFeedback, allTest = Tasks.taskSets paretoCount feedbackCount holdoutCount
        let pareto = allPareto |> List.indexed
        let feedback = allFeedback
        let testSet = allTest |> List.indexed
        let baselineCfg = quietConfig budget miniBatch feedback.Length GepaMode
        let sys = createInitialCandidate seed
        printfn $"Compare settings: endpoint={llamaCppEndpoint}; model={llamaCppModel}; seed={seed}; mode={mode}; budget={budget}; mini_batch={miniBatch}; pareto={pareto.Length}; feedback={feedback.Length}; holdout={testSet.Length}"
        let baselineOverride =
            Environment.GetEnvironmentVariable("FSGEPA_GSM8K_COMPARE_BASELINE_OVERRIDE")
            |> checkEmpty
            |> Option.bind (fun _ -> Some (envOrDefaultFloat "FSGEPA_GSM8K_COMPARE_BASELINE_OVERRIDE" Double.NaN))
            |> Option.filter (Double.IsNaN >> not)
        let baseline =
            match baselineOverride with
            | Some value ->
                printfn $"Shared baseline holdout score: %0.4f{value} (override)"
                value
            | None ->
                let computed = FsGepa.Run.Scoring.averageScore baselineCfg sys testSet
                printfn $"Shared baseline holdout score: %0.4f{computed}"
                computed
        let! gepaResult =
            async {
                match mode with
                | Both
                | GepaOnly ->
                    let! result = runExperiment "GEPA" (quietConfig budget miniBatch feedback.Length GepaMode) baseline sys pareto feedback testSet
                    return Some result
                | VistaOnly ->
                    return None
            }
        let! vistaResult =
            async {
                match mode with
                | Both
                | VistaOnly ->
                    let! result = runExperiment "VISTA" (quietConfig budget miniBatch feedback.Length VistaMode) baseline sys pareto feedback testSet
                    return Some result
                | GepaOnly ->
                    return None
            }
        match gepaResult,vistaResult with
        | Some gepa, Some vista ->
            let winner =
                [gepa; vista]
                |> List.maxBy _.optimized
            printfn $"Winner: {winner.name} at %0.4f{winner.optimized}"
        | _ -> ()
        return gepaResult,vistaResult
    }

    let start seed = async {
        let paretoCount = envOrDefaultInt "FSGEPA_GSM8K_START_PARETO" 50
        let feedbackCount = envOrDefaultInt "FSGEPA_GSM8K_START_FEEDBACK" 50
        let holdoutCount = envOrDefaultInt "FSGEPA_GSM8K_START_HOLDOUT" 200
        let tPareto, tFeedback, tTest = Tasks.taskSets paretoCount feedbackCount holdoutCount
        let testSet = tTest |> List.indexed
        let tPareto = List.indexed tPareto
        // Align the standalone start path with the safe concurrency cap (honors env override)
        let cfg = { quietConfig 20 8 tFeedback.Length VistaMode with telemetry_channel = Some channel; flow_parallelism = envOrDefaultInt "FSGEPA_FLOW_PARALLELISM" 5 }
        let sys = createInitialCandidate seed
        Log.info "Establishing baseline score"
        let initScore = FsGepa.Run.Scoring.averageScore cfg sys testSet
        Log.info $"Baseline score: {initScore}"
        let ts0 = DateTime.UtcNow
        let! finalRunState = Gepa.run cfg sys tPareto tFeedback
        let ts1 = DateTime.UtcNow
        channel.Writer.TryComplete() |> ignore
        let sysStar = finalRunState.candidates |> List.maxBy _.avgScore.Value
        let optimizedScore = FsGepa.Run.Scoring.averageScore cfg sysStar.sys testSet
        printfn $"Holdout Set: Baseline score = %0.4f{initScore}; Optimized score = %0.4f{optimizedScore}; Elapsed: {ts1 - ts0}"
        return finalRunState
    }
