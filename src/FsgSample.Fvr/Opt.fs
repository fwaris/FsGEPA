namespace FsgSample.Fvr
open System
open System.Text.Json
open System.Text.Json.Serialization
open FSharp.Control
open FsGepa

module Opt = 

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
                ENDPOINT = "http://localhost:8080/v1"
            }
            backendType = FsgGenAI.BackendType.ChatCompletions
        }

    let exampleBackendLlamaCppGptOss : FsgGenAI.Backend =     
        {
            endpoint =  {
                API_KEY = "cannot be empty"
                ENDPOINT = "http://localhost:8080/v1"
            }
            backendType = FsgGenAI.BackendType.ChatCompletionsHarmony
        }

    let m1 = 
        { GeModule.Default with
            prompt = {text="Given the fields `claim` and `supporting_facts`, produce the field `summary`."}
            moduleId = "summarize"
        }

    let m2 = 
        { GeModule.Default with
            prompt = {text="Given the fields `claim` and `summary`, produce the field `answer`."}
            moduleId = "predict"            
            outputSchema = Tasks.answers |> String.concat "|" |> Some //not currently used
        }

    //intermediate types
    type Claim_SupportingFacts = {claim:string; supporting_facts:string}
    type Summary = {summary:string}
    type Claim_Summary = {claim:string; summary:string}

    let step1 cfg (geModule:GeModule) (fr:FeverousInput) = async {
        let model = geModule.model |> Option.defaultValue cfg.default_model
        let taskInput = JsonSerializer.Serialize({claim=fr.claim; supporting_facts = fr.document}, Utils.openAIResponseSerOpts)
        let! resp = 
            cfg.generator.generate 
                model 
                (Some geModule.prompt.text) 
                [{role="user"; content=taskInput}] 
                (Some typeof<Summary>)
                (Some {GenOpts.Default with temperature=Some 0.2f})
        let summary = try JsonSerializer.Deserialize<Summary>(resp.output, Utils.openAIResponseSerOpts).summary with _ -> resp.output
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
                (Some typeof<Answer>) 
                (Some {GenOpts.Default with temperature=Some 0.2f})
        return {moduleId = geModule.moduleId; taskInput=taskInput; response=resp.output; reasoning=resp.thoughts}        
    }

    let flow cfg (modules:Map<string,GeModule>) input : Async<FlowResult<Answer>> =  async {
        let m1 = modules.["summarize"]
        let m2 = modules.["predict"]
        let! trace1 = step1 cfg m1 input 
        let! trace2 = step2 cfg m2 input trace1.response
        let ans = JsonSerializer.Deserialize<Answer>(trace2.response)
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

    let config_GptOss feedbackSize = 
        let generate = FsgGenAI.GenAI.createDefault exampleBackendLlamaCppGptOss
        {Config.CreateDefault generate feedbackSize {id="gpt-oss-20b"} with 
            telemetry_channel = Some channel
            mini_batch_size = 20
        }

    let config_OpenAI feedbackSize = 
        let generate = FsgGenAI.GenAI.createDefault backendOpenAI
        {Config.CreateDefault generate feedbackSize {id="gpt-5-mini"} with 
            telemetry_channel = Some channel
        }

    let start() = async {
        let tPareto,tFeedback,tTest = Tasks.taskSets()
        let testSet = tTest |> Seq.indexed |> Seq.truncate 100 |> Seq.toList
        let tPareto = List.indexed tPareto
        let cfg = config_GptOss tFeedback.Length
        //let cfg = config_OpenAI tFeedback.Length
        let sys = createInitialCandidate()     
        Log.info "Establishing baseline score"
        let initScore = FsGepa.Run.Scoring.averageScore cfg sys  testSet
        Log.info $"Baseline score: {initScore}"
        let! finalRunState =  Gepa.run cfg sys tPareto tFeedback 
        channel.Writer.TryComplete() |> ignore
        let sysStar = finalRunState.candidates |> List.maxBy _.avgScore.Value
        let optimizedScore = FsGepa.Run.Scoring.averageScore cfg sysStar.sys testSet
        printfn $"Holdout Set: Baseline score = %0.2f{initScore}; Optimized score = %0.2f{optimizedScore}"
        return finalRunState
    }
