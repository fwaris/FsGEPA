namespace FsgSample.Fvr
open System
open System.Text.Json
open System.Text.Json.Serialization
open FSharp.Control
open FsGepa

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

    let private llamaCppEndpoint = envOrDefault "FSGEPA_LLAMACPP_ENDPOINT" "http://localhost:8081/v1"
    let private llamaCppModel = envOrDefault "FSGEPA_LLAMACPP_MODEL" "gpt-oss-20b-mxfp4.gguf"

    let backendOpenAI : FsgGenAI.Backend =     
        {
            endpoint =  {
                API_KEY = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                ENDPOINT = "https://api.openai.com/v1"
            }

            backendType = FsgGenAI.BackendType.Responses
        }

    let exampleBackendLlamaCpp : FsgGenAI.Backend =     
        {
            endpoint =  {
                API_KEY = "cannot be empty"
                ENDPOINT = llamaCppEndpoint
            }
            backendType = FsgGenAI.BackendType.ChatCompletions
        }

    let exampleBackendLlamaCppGptOss : FsgGenAI.Backend =     
        {
            endpoint =  {
                API_KEY = "cannot be empty"
                ENDPOINT = llamaCppEndpoint
            }
            backendType = FsgGenAI.BackendType.ChatCompletionsHarmony
        }

    let m1 = 
        { GeModule.Default with
            prompt = {text="Given the fields `claim` and `supporting_facts`, return a JSON object with the field `summary`."}
            moduleId = "summarize"
        }

    let m2 = 
        { GeModule.Default with
            prompt = {text="Given the fields `claim` and `summary`, return a JSON object with the field `answer`. The value must be one of `SUPPORTS`, `REFUTES`, or `NOT_ENOUGH_INFO`."}
            moduleId = "predict"            
            outputSchema = Tasks.answers |> String.concat "|" |> Some //not currently used
        }

    //intermediate types
    type Claim_SupportingFacts = {claim:string; supporting_facts:string}
    type Summary = {summary:string}
    type Claim_Summary = {claim:string; summary:string}

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

    let private parseSummaryOutput raw =
        let cleaned = stripHarmonyEnvelope raw
        let candidate = tryExtractJsonObject cleaned |> Option.defaultValue cleaned
        try
            JsonSerializer.Deserialize<Summary>(candidate, Utils.openAIResponseSerOpts).summary
        with _ ->
            candidate

    let private tryParseAnswerLabel (text:string) =
        let normalized = text.Trim().ToUpperInvariant()
        if normalized.Contains("NOT_ENOUGH_INFO") || normalized.Contains("NOT ENOUGH INFO") then
            Some AnswerType.NOT_ENOUGH_INFO
        elif normalized.Contains("REFUTES") || normalized.Contains("REFUTE") then
            Some AnswerType.REFUTES
        elif normalized.Contains("SUPPORTS") || normalized.Contains("SUPPORT") then
            Some AnswerType.SUPPORTS
        else
            None

    let private parseAnswerOutput raw =
        let cleaned = stripHarmonyEnvelope raw
        let candidate = tryExtractJsonObject cleaned |> Option.defaultValue cleaned
        let tryStructured () =
            try
                JsonSerializer.Deserialize<Answer>(candidate, Utils.openAIResponseSerOpts)
                |> Some
            with _ ->
                None
        match tryStructured() with
        | Some answer -> answer
        | None ->
            match tryParseAnswerLabel candidate with
            | Some answer -> {answer = answer}
            | None -> failwithf "Could not parse answer from model output: %s" (candidate |> shorten 200)

    let step1 cfg (geModule:GeModule) (fr:FeverousInput) = async {
        let model = geModule.model |> Option.defaultValue cfg.default_model
        let taskInput = JsonSerializer.Serialize({claim=fr.claim; supporting_facts = fr.document}, Utils.openAIResponseSerOpts)
        let! resp = 
            cfg.generator.generate 
                model 
                (Some geModule.prompt.text) 
                [{role="user"; content=taskInput}] 
                None
                (Some {GenOpts.Default with temperature=Some 0.2f; max_tokens=Some 384})
        let summarySource =
            if notEmpty resp.output then resp.output
            else resp.thoughts |> Option.defaultValue ""
        let summary = parseSummaryOutput summarySource
        return {moduleId = geModule.moduleId; taskInput=taskInput; response=summary; reasoning=resp.thoughts}        
    }

    let step2 cfg (geModule:GeModule) (fr:FeverousInput) summary = async {
        let model = geModule.model |> Option.defaultValue cfg.default_model
        let taskInput = JsonSerializer.Serialize({claim = fr.claim; summary = summary}, Utils.openAIResponseSerOpts)
        let! resp = 
            cfg.generator.generate 
                model 
                (Some geModule.prompt.text) 
                [{role="user"; content=taskInput}] 
                None
                (Some {GenOpts.Default with temperature=Some 0.2f; max_tokens=Some 192})
        let answerSource =
            if notEmpty resp.output then resp.output
            else resp.thoughts |> Option.defaultValue ""
        return {moduleId = geModule.moduleId; taskInput=taskInput; response=answerSource; reasoning=resp.thoughts}        
    }

    let flow cfg (modules:Map<string,GeModule>) input : Async<FlowResult<Answer>> =  async {
        let m1 = modules.["summarize"]
        let m2 = modules.["predict"]
        let! trace1 = step1 cfg m1 input 
        let! trace2 = step2 cfg m2 input trace1.response
        let ans = parseAnswerOutput trace2.response
        return
            {
                output = ans
                traces = [trace1; trace2]
            }
    }

    let createInitialCandidate() = 
        {
            modules = [m1; m2] |> List.map (fun m -> m.moduleId,m) |> Map.ofList
            flow = flow
        }

    let channel = System.Threading.Channels.Channel.CreateBounded<Telemetry>(10)

    channel.Reader.ReadAllAsync()
    |> AsyncSeq.ofAsyncEnum
    |> AsyncSeq.iter(printfn "%A")
    |> Async.Start

    let private config_GptOssBase feedbackSize = 
        let generate = FsgGenAI.GenAI.createDefault exampleBackendLlamaCppGptOss
        {Config.CreateDefault generate feedbackSize {id=llamaCppModel} with 
            mini_batch_size = 20
        }

    let config_GptOss feedbackSize = 
        {config_GptOssBase feedbackSize with 
            telemetry_channel = Some channel
        }

    let config_VistaGptOss feedbackSize =
        {
            config_GptOssBase feedbackSize with
                telemetry_channel = Some channel
                optimizer_mode = VistaMode
                vista = {
                    VistaConfig.Default with
                        hypothesis_count = 4
                        hypotheses_to_validate = 3
                        epsilon_greedy = 0.20
                        random_restart_stagnation = Some 4
                }
        }

    let config_OpenAI feedbackSize = 
        let generate = FsgGenAI.GenAI.createDefault backendOpenAI
        {Config.CreateDefault generate feedbackSize {id="gpt-5-mini"} with 
            telemetry_channel = Some channel
        }

    let config_VistaOpenAI feedbackSize =
        {
            config_OpenAI feedbackSize with
                optimizer_mode = VistaMode
                vista = {
                    VistaConfig.Default with
                        reflector_model = Some {id="gpt-5-mini"}
                }
        }

    type ComparisonResult = {
        name : string
        baseline : float
        optimized : float
        improvement : float
        elapsed : TimeSpan
        candidateCount : int
    }

    let private quietConfig budget miniBatch feedbackSize mode =
        let baseCfg =
            match mode with
            | GepaMode -> config_GptOssBase feedbackSize
            | VistaMode ->
                {
                    config_GptOssBase feedbackSize with
                        optimizer_mode = VistaMode
                        vista = {
                            VistaConfig.Default with
                                hypothesis_count = 3
                                hypotheses_to_validate = 2
                                epsilon_greedy = 0.20
                                random_restart_stagnation = Some 2
                        }
                }
        {
            baseCfg with
                budget = budget
                mini_batch_size = miniBatch
                telemetry_channel = None
        }

    let private runExperiment name cfg baseline sys tPareto tFeedback testSet = async {
        let ts0 = DateTime.UtcNow
        let! finalRunState = Gepa.run cfg sys tPareto tFeedback
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
        printfn $"{result.name}: baseline=%0.2f{result.baseline}; optimized=%0.2f{result.optimized}; improvement=%+0.2f{result.improvement}; candidates={result.candidateCount}; elapsed={result.elapsed}"
        return result
    }

    let compare() = async {
        let budget = envOrDefaultInt "FSGEPA_COMPARE_BUDGET" 2
        let miniBatch = envOrDefaultInt "FSGEPA_COMPARE_MINI_BATCH" 4
        let paretoCount = envOrDefaultInt "FSGEPA_COMPARE_PARETO" 12
        let feedbackCount = envOrDefaultInt "FSGEPA_COMPARE_FEEDBACK" 8
        let holdoutCount = envOrDefaultInt "FSGEPA_COMPARE_HOLDOUT" 12
        let allPareto,allFeedback,allTest = Tasks.taskSets()
        let pareto = allPareto |> List.truncate paretoCount |> List.indexed
        let feedback = allFeedback |> List.truncate feedbackCount
        let testSet = allTest |> List.truncate holdoutCount |> List.indexed
        let baselineCfg = quietConfig budget miniBatch feedback.Length GepaMode
        let sys = createInitialCandidate()
        printfn $"Compare settings: endpoint={llamaCppEndpoint}; model={llamaCppModel}; budget={budget}; mini_batch={miniBatch}; pareto={pareto.Length}; feedback={feedback.Length}; holdout={testSet.Length}"
        let baseline = FsGepa.Run.Scoring.averageScore baselineCfg sys testSet
        printfn $"Shared baseline holdout score: %0.2f{baseline}"
        let! gepaResult = runExperiment "GEPA" (quietConfig budget miniBatch feedback.Length GepaMode) baseline sys pareto feedback testSet
        let! vistaResult = runExperiment "VISTA" (quietConfig budget miniBatch feedback.Length VistaMode) baseline sys pareto feedback testSet
        let winner =
            [gepaResult; vistaResult]
            |> List.maxBy (fun result -> result.optimized)
        printfn $"Winner: {winner.name} at %0.2f{winner.optimized}"
        return gepaResult,vistaResult
    }

    let start() = async {
        let tPareto,tFeedback,tTest = Tasks.taskSets()
        let testSet = tTest |> Seq.indexed |> Seq.truncate 100 |> Seq.toList
        let tPareto = List.indexed tPareto
        let cfg = config_VistaGptOss tFeedback.Length
        //let cfg = config_GptOss tFeedback.Length
        //let cfg = config_VistaOpenAI tFeedback.Length
        let sys = createInitialCandidate()     
        Log.info "Establishing baseline score"
        let initScore = FsGepa.Run.Scoring.averageScore cfg sys  testSet
        Log.info $"Baseline score: {initScore}"
        let ts0 = DateTime.Now
        let! finalRunState =  Gepa.run cfg sys tPareto tFeedback 
        let ts1 = DateTime.Now
        channel.Writer.TryComplete() |> ignore
        let sysStar = finalRunState.candidates |> List.maxBy _.avgScore.Value
        let optimizedScore = FsGepa.Run.Scoring.averageScore cfg sysStar.sys testSet
        printfn $"Holdout Set: Baseline score = %0.2f{initScore}; Optimized score = %0.2f{optimizedScore}; Elapsed: {ts1-ts0}"
        return finalRunState
    }
