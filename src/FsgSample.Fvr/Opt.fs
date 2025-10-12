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


    let generate() = FsgGenAI.GenAI.createDefault backendOpenAI

     //results in {{$v}} to match semantic kernel template variable format
    let formatVar (v:string) =  $"{{{{${v}}}}}"

    let validatePrompt expectedNames (prompt:string) = async{
        let templateVars = Template.extractVarNames prompt
        let varCounts = templateVars |> List.countBy id
        let varSet = varCounts |> List.map fst |> set
        let presentVars = Set.intersect varSet expectedNames
        if presentVars.Count = expectedNames.Count && varCounts |> List.forall ((fun (_,c) -> c = 1)) then 
            return None
        else
            let missing = Set.difference expectedNames varSet |> Seq.map formatVar |> String.concat ","
            let highCounts = varCounts |> List.filter (fun (_,c) -> c > 1) |> List.map (fst>>formatVar) |> String.concat ","
            let missingFeedback = if isEmpty missing then "" else $"# Ensure following template variables are present: {missing}"
            let highCountFeedback = if isEmpty highCounts then "" else $"# Ensure there is only ONE instance for each the following template variables: {highCounts}"
            let feedback = $"{missingFeedback}{highCountFeedback}"
            return Some feedback
        }

    let m1Vars = set ["claim";"content"]
    let m1VarsStr = m1Vars |> Seq.map formatVar |> String.concat "," 
    let m2Vars = set ["claim";"summary"]
    let m2VarsStr = m2Vars |> Seq.map formatVar |> String.concat "," 

    let m1Meta = 
        {
            metaPrompt = Prompts.metaPrompt + $"\n Ensure the following template variables are present: {m1VarsStr}"
            validate = validatePrompt m1Vars
        }

    let m2Meta = 
        {
            metaPrompt = Prompts.metaPrompt + $"\n Ensure the following template variables are present: {m2VarsStr}"
            validate = validatePrompt m2Vars
        }

    let m1 = 
        { GeModule.Default with
            prompt = {text="given the [CLAIM]\n{{$claim}}. Extract and summarize the supporting facts from the [CONTENT]\n{{$content}}"}
            moduleId = "summarize"
            metaPrompt = Some m1Meta
        }

    let m2 = 
        { GeModule.Default with
            prompt = {text="given the [CLAIM]\n{{$claim}}. And the [SUMMARY]\n{{$summary}}. Determine if the facts support the claim or not"}
            moduleId = "predict"            
            metaPrompt = Some m2Meta
            outputSchema = Tasks.answers |> String.concat "|" |> Some
        }

    let step1 cfg (m1:GeModule) fr = async {
        let prompt = ["claim", fr.claim :> obj; "content", fr.document] |> Prompts.renderPrompt m1.prompt.text
        let model = m1.model |> Option.defaultValue cfg.default_model
        let! resp = cfg.generate.generate model  None [{role="user"; content=prompt}] None 
        return {moduleId = m1.moduleId; inputPrompt=prompt; response=resp.output; reasoning=resp.thoughts}        
    }

    let step2 cfg (m2:GeModule) fr summary = async {
        let prompt = ["claim", fr.claim :> obj; "summary", summary] |> Prompts.renderPrompt m2.prompt.text
        let model = m2.model |> Option.defaultValue cfg.default_model
        let! resp = cfg.generate.generate model  None [{role="user"; content=prompt}] (Some typeof<Answer>)
        return {moduleId = m2.moduleId; inputPrompt=prompt; response=resp.output; reasoning=resp.thoughts}        
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
        }

    let config_OpenAI feedbackSize = 
        let generate = FsgGenAI.GenAI.createDefault backendOpenAI
        {Config.CreateDefault generate feedbackSize {id="gpt-5-mini"} with 
            telemetry_channel = Some channel
        }

    let start() = async {
        let tPareto,tMB,tTest = Tasks.taskSets()
        let testSet = tTest |> Seq.indexed |> Seq.truncate 20 |> Seq.toList
        let tPareto = List.indexed tPareto
        let cfg = config_GptOss tMB.Length
        // cfg = config_OpenAI tMB.Length
        let sys = createInitialCandidate()     
        let initScore = FsGepa.Run.Scoring.averageScore cfg sys  testSet
        let! finalRunState =  Gepa.run cfg sys tPareto tMB 
        channel.Writer.TryComplete() |> ignore
        let sysStar = finalRunState.candidates |> List.maxBy _.avgScore.Value
        let optimizedScore = FsGepa.Run.Scoring.averageScore cfg sysStar.sys testSet
        printfn $"Holdout Set: Initial score = %0.2f{initScore}; Optimized score = %0.2f{optimizedScore}"
        return finalRunState
    }
