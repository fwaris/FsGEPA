namespace FsGepa.GenAI

open Microsoft.Extensions.AI
open Harmony.Microsoft.Extensions.AI

#nowarn "57"

[<RequireQualifiedAccess>]
type BackendType = 

    ///Traditional chat completions endpoint
    | ChatCompletions 

    /// <summary>
    /// Use ChatCompletionsHarmony if the `model + inference server` returns think tokens in harmony format.<br />
    /// For example, `gpt-oss-20b + llama.cpp` returns thought stream in the main output as:<br />
    /// `&lt;|channel|&gt;analysis&lt;|message|&gt;  ...thoughts...  &lt;|end|&gt; ...actual output...`<br />
    /// </summary>
    | ChatCompletionsHarmony

    ///Response API endpoint that can return reasoning tokens
    | Responses

type ApiEndpoint =
    {
        API_KEY : string
        ENDPOINT : string
    }

type Backend = {
    endpoint : ApiEndpoint
    backendType : BackendType
}

module ResponsesApi = 
    open OpenAI
    open OpenAI.Responses
    open Microsoft.Extensions.AI
    open FSharp.Control
    open System.Text.Json
    open System
    open System.Collections.Generic
    open FsGepa

    let private prefix_models_not_supporting_temp =  ["gpt-5"]
  
    let supportsTemp (model:string) =
        prefix_models_not_supporting_temp
        |> List.exists (fun p -> model.StartsWith(p,StringComparison.CurrentCultureIgnoreCase)) |> not

    let private textContents (message:ChatMessage) =
        message.Contents
        |> Seq.choose (function :? TextContent as tc -> Some tc.Text | _ -> None)

    let private toResponseInputParts (message:ChatMessage) =
        message
        |> textContents
        |> Seq.map Responses.ResponseContentPart.CreateInputTextPart

    let private toResponseOutputParts (message:ChatMessage) =
        message
        |> textContents
        |> Seq.map (fun text -> Responses.ResponseContentPart.CreateOutputTextPart(text, []))

    let internal toResponsesItem (message:ChatMessage) : Responses.ResponseItem =
        match message.Role with
        | role when role = ChatRole.System ->
            message
            |> toResponseInputParts
            |> Responses.ResponseItem.CreateSystemMessageItem
            :> _
        | role when role = ChatRole.Assistant ->
            message
            |> toResponseOutputParts
            |> Responses.ResponseItem.CreateAssistantMessageItem
            :> _
        | role when role = ChatRole.User ->
            message
            |> toResponseInputParts
            |> Responses.ResponseItem.CreateUserMessageItem
            :> _
        | r -> failwithf "Unsupported chat role %A for Responses API" r

    let toTextOutput (item:Responses.ResponseItem) =
        match item with
        | :? Responses.MessageResponseItem as m -> m.Content |> Seq.map _.Text
        | _ -> Seq.empty

    let toReasoningOutput (item:Responses.ResponseItem) =
        match item with
        | :? Responses.ReasoningResponseItem as m ->
            m.SummaryParts
            |> Seq.choose (function :? Responses.ReasoningSummaryTextPart as t -> Some t.Text | _ -> None)
        | _ -> Seq.empty

    let internal callGenerateResponse (respClient:Responses.ResponsesClient) (opts:Responses.CreateResponseOptions) = async {
        try 
            let! resp = respClient.CreateResponseAsync(opts) |> Async.AwaitTask
            let text = resp.Value.OutputItems |> Seq.collect toTextOutput |> String.concat ""
            let reasoning = resp.Value.OutputItems |> Seq.collect toReasoningOutput |> String.concat ""
            return text, reasoning            
        with ex ->
            Log.exn (ex,"callGenerateResponse")
            return raise ex
    }

    let internal generate model (chat:ChatMessage list) (outputFormat:JsonElement option) (gopts:GenOpts option) (endpoint:ApiEndpoint) = async {
        let copts = OpenAI.OpenAIClientOptions(Endpoint = Uri endpoint.ENDPOINT)
        let respClient = Responses.ResponsesClient(model, ClientModel.ApiKeyCredential(endpoint.API_KEY), copts)
        let responseItems = chat |> Seq.map toResponsesItem
        let opts = Responses.CreateResponseOptions()
        responseItems |> Seq.iter opts.InputItems.Add
        gopts
        |> Option.iter(fun o -> 
            if supportsTemp model then             
                o.temperature |> Option.iter (fun t -> opts.Temperature <- t) //not supported by some models
            o.max_tokens |> Option.iter (fun t -> opts.MaxOutputTokenCount <- t))
        opts.ReasoningOptions <- Responses.ResponseReasoningOptions(
           ReasoningEffortLevel = Responses.ResponseReasoningEffortLevel.High,
            ReasoningSummaryVerbosity = Responses.ResponseReasoningSummaryVerbosity.Detailed)
        match outputFormat with 
        | Some fmt -> 
            let schemaJson = fmt.GetRawText()
            let schemaData = BinaryData(schemaJson)
            let textOpts = Responses.ResponseTextOptions()
            textOpts.TextFormat <- Responses.ResponseTextFormat.CreateJsonSchemaFormat("schema", schemaData, jsonSchemaIsStrict = true)
            opts.TextOptions <- textOpts
        | None -> ()
        return! callGenerateResponse respClient opts
    }

module CompletionsApi = 
    open OpenAI
    open OpenAI.Chat
    open Microsoft.Extensions.AI
    open FSharp.Control
    open System.Text.Json
    open System
    open FsGepa

    let private collectContent<'T when 'T :> AIContent> (selector:'T -> string) (messages:seq<ChatMessage>) =
        messages
        |> Seq.collect (fun message ->
            message.Contents
            |> Seq.choose (function :? 'T as content -> Some (selector content) | _ -> None))
        |> String.concat ""

    let internal callGenerate (client:IChatClient) (chat:ChatMessage list) (opts:ChatOptions) = async {        
        try
            let! response = client.GetResponseAsync(chat, opts) |> Async.AwaitTask
            let rawOutput = collectContent<TextContent> (fun c -> c.Text) response.Messages
            let rawReasoning = collectContent<TextReasoningContent> (fun c -> c.Text) response.Messages
            let output, harmonyReasoning = HarmonyGrammar.splitHarmony rawOutput
            let reasoning =
                [ rawReasoning; harmonyReasoning |> Option.defaultValue "" ]
                |> List.choose checkEmpty
                |> String.concat "\n\n"
            return output, reasoning
        with ex ->
            Log.exn(ex,"callGenerate")
            return raise ex
    }

    let internal generate model chat (outputFormat:JsonElement option) (gopts:GenOpts option) endpoint useHarmony = async {
        let copts = OpenAI.OpenAIClientOptions(Endpoint = Uri endpoint.ENDPOINT)
        let chatClient = ChatClient(model, ClientModel.ApiKeyCredential(endpoint.API_KEY), copts)
        let baseClient = OpenAIClientExtensions.AsIChatClient(chatClient)
        let opts = ChatOptions()
        opts.ModelId <- model
        gopts
        |> Option.iter(fun o -> 
            if ResponsesApi.supportsTemp model then 
                o.temperature |> Option.iter (fun t -> opts.Temperature <- Nullable t)
            o.max_tokens |> Option.iter (fun t -> opts.MaxOutputTokens <- Nullable t))
        match outputFormat with 
        | Some fmt -> opts.ResponseFormat <- ChatResponseFormat.ForJsonSchema(fmt)
        | None -> ()
        if useHarmony then
            use harmonyClient = new HarmonyChatClient(baseClient)
            return! callGenerate (harmonyClient :> IChatClient) chat opts
        else
            use disposableClient = baseClient
            return! callGenerate disposableClient chat opts
    }

module Api =
    open Microsoft.Extensions.AI
    open FSharp.Control
    open System.Text.Json
    open System
    open FsGepa

    let private normalizeRole (role:string) =
        match role with
        | null -> failwith "Role cannot be null"
        | r when r.Equals("user", StringComparison.OrdinalIgnoreCase) -> ChatRole.User
        | r when r.Equals("assistant", StringComparison.OrdinalIgnoreCase) -> ChatRole.Assistant
        | r when r.Equals("system", StringComparison.OrdinalIgnoreCase) -> ChatRole.System
        | _ -> failwithf "Unknown role %s" role

    let private createChatMessage (role:ChatRole) (content:string) =
        ChatMessage(role, content)

    let generate (backend:Backend) (systemMessage:string option) (chat:ChatMessage list) (responseFormat:Type option) (opts:GenOpts option) (model:Model) = async {
        let outputFormat = responseFormat |> Option.map SchemaUtils.toSchema
        match backend.backendType with 
        | BackendType.Responses -> return! ResponsesApi.generate model.id chat outputFormat opts backend.endpoint
        | BackendType.ChatCompletions -> return! CompletionsApi.generate model.id chat outputFormat opts backend.endpoint false
        | BackendType.ChatCompletionsHarmony -> return! CompletionsApi.generate model.id chat outputFormat opts backend.endpoint true        
    }
        
    let createDefault  backend =
        {new IGenerate with         
            member this.generate(model: Model) (systemMessage: string option) (messages: GenMessage list) (responseFormat: Type option) (opts:GenOpts option): Async<GenerateResponse> = async {
                let chat = 
                    [
                        match systemMessage with
                        | Some sm -> yield createChatMessage ChatRole.System sm
                        | None -> ()
                        for m in messages do
                            let role = normalizeRole m.role
                            yield createChatMessage role m.content
                    ]
                let! output,thoughts = generate backend systemMessage chat responseFormat opts model
                return {output=output; thoughts=checkEmpty thoughts}
            }                
        }
        
