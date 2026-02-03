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

module Schema = 
    open System
    open Microsoft.Extensions.AI
    open System.Text.Json
    open System.Text.Json.Serialization

    ///generate a schema from the given JSON 
    let generate(t:Type): JsonElement =
        let createOptions =
            AIJsonSchemaCreateOptions(
                TransformOptions = AIJsonSchemaTransformOptions(DisallowAdditionalProperties = true))

        let serializerOptions = JsonSerializerOptions(AIJsonUtilities.DefaultOptions)

        let fsharpConverterTypeName = "System.Text.Json.Serialization.Converters.FSharpTypeConverterFactory, System.Text.Json"
        let fsharpConverterType = Type.GetType(fsharpConverterTypeName)

        match fsharpConverterType with
        | null -> ()
        | converterType ->
            let alreadyAdded =
                serializerOptions.Converters
                |> Seq.exists (fun c -> converterType.IsAssignableFrom(c.GetType()))

            if not alreadyAdded then
                let instance = Activator.CreateInstance(converterType)
                match instance with
                | :? JsonConverter as converter -> serializerOptions.Converters.Add(converter)
                | _ -> ()

        let hasStringEnumConverter =
            serializerOptions.Converters
            |> Seq.exists (fun c -> c :? JsonStringEnumConverter)

        if not hasStringEnumConverter then
            serializerOptions.Converters.Add(JsonStringEnumConverter())

        AIJsonUtilities.CreateJsonSchema(
            t,
            description = t.Name,
            serializerOptions = serializerOptions,
            inferenceOptions = createOptions)

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

    let rec internal callGenerateResponse attempts (respClient:Responses.ResponsesClient) (opts:Responses.CreateResponseOptions) = async {
        try 
            let! resp = respClient.CreateResponseAsync(opts) |> Async.AwaitTask
            let text = resp.Value.OutputItems |> Seq.collect toTextOutput |> String.concat ""
            let reasoning = resp.Value.OutputItems |> Seq.collect toReasoningOutput |> String.concat ""
            return text, reasoning            
        with ex -> 
            if attempts > 0 then 
                Log.warn $"callGenerateResponse failed, attempts remain {attempts - 1}. Error {ex.Message}"
                do! Async.Sleep 3000
                return! callGenerateResponse (attempts - 1) respClient opts
            else
                Log.exn (ex,"callGenerateResponse")
                return raise ex
    }

    let internal generate attempts model (chat:ChatMessage list) (outputFormat:JsonElement option) (gopts:GenOpts option) (endpoint:ApiEndpoint) = async {
        let copts = OpenAI.OpenAIClientOptions(Endpoint = Uri endpoint.ENDPOINT)
        let respClient = Responses.ResponsesClient(model, ClientModel.ApiKeyCredential(endpoint.API_KEY), copts)
        let responseItems = chat |> Seq.map toResponsesItem
        let opts = Responses.CreateResponseOptions()
        responseItems |> Seq.iter opts.InputItems.Add
        gopts
        |> Option.iter(fun o -> 
            if supportsTemp model |> not then             
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
        return! callGenerateResponse 5 respClient opts
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

    let rec internal callGenerate attempts (client:IChatClient) (chat:ChatMessage list) (opts:ChatOptions) = async {        
        try
            let! response = client.GetResponseAsync(chat, opts) |> Async.AwaitTask
            let output = collectContent<TextContent> (fun c -> c.Text) response.Messages
            let reasoning = collectContent<TextReasoningContent> (fun c -> c.Text) response.Messages
            return output, reasoning
        with ex -> 
            if attempts > 0 then    
                Log.warn $"llm call failed attempts left {attempts - 1}: Error: {ex.Message}"
                do! Async.Sleep 3000
                return! callGenerate (attempts - 1) client chat opts
            else 
                Log.exn(ex,"callGenerate")
                return raise ex
    }

    let internal generate attempts model chat (outputFormat:JsonElement option) (gopts:GenOpts option) endpoint useHarmony = async {
        let copts = OpenAI.OpenAIClientOptions(Endpoint = Uri endpoint.ENDPOINT)
        let chatClient = ChatClient(model, ClientModel.ApiKeyCredential(endpoint.API_KEY), copts)
        let baseClient = OpenAIClientExtensions.AsIChatClient(chatClient)
        let opts = ChatOptions()
        opts.ModelId <- model
        gopts
        |> Option.iter(fun o -> 
            if ResponsesApi.supportsTemp model |> not then 
                o.temperature |> Option.iter (fun t -> opts.Temperature <- Nullable t)
            o.max_tokens |> Option.iter (fun t -> opts.MaxOutputTokens <- Nullable t))
        match outputFormat with 
        | Some fmt -> opts.ResponseFormat <- ChatResponseFormat.ForJsonSchema(fmt)
        | None -> ()
        if useHarmony then
            use harmonyClient = new HarmonyChatClient(baseClient)
            return! callGenerate attempts (harmonyClient :> IChatClient) chat opts
        else
            use disposableClient = baseClient
            return! callGenerate attempts disposableClient chat opts
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
        let outputFormat = responseFormat |> Option.map Schema.generate
        match backend.backendType with 
        | BackendType.Responses -> return! ResponsesApi.generate 5 model.id chat outputFormat opts backend.endpoint
        | BackendType.ChatCompletions -> return! CompletionsApi.generate 5 model.id chat outputFormat opts backend.endpoint false
        | BackendType.ChatCompletionsHarmony -> return! CompletionsApi.generate 5 model.id chat outputFormat opts backend.endpoint true        
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
        