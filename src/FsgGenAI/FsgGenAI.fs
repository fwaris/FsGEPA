namespace FsgGenAI
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
    open OpenAI.Chat
    open FSharp.Control
    open System.Text.Json
    open System
    open FsGepa

    let internal toTextContentParts (xs:ChatMessageContentPart seq)  =
        xs
        |> Seq.map _.Text
        |> Seq.map Responses.ResponseContentPart.CreateInputTextPart 

    let internal toTextContentPartsOut (xs:ChatMessageContentPart seq)  =
        xs
        |> Seq.map _.Text
        |> Seq.map (fun p -> Responses.ResponseContentPart.CreateOutputTextPart(p,[]))
    
    let internal toResponsesItem (m:ChatMessage) : Responses.ResponseItem=
        match m with 
        | :? SystemChatMessage as c -> 
            c.Content
            |> toTextContentParts
            |> Responses.ResponseItem.CreateSystemMessageItem
            :> _
        | :? UserChatMessage as c -> 
            c.Content
            |> toTextContentParts
            |> Responses.ResponseItem.CreateUserMessageItem
            :> _
        | :? AssistantChatMessage as c -> 
            c.Content
            |> toTextContentPartsOut
            |> Responses.ResponseItem.CreateAssistantMessageItem
            :> _
        | x -> failwith $"message type not handled {x}"

    let toTextOutput(ri:Responses.ResponseItem) =
        match ri with 
        | :? Responses.MessageResponseItem as m -> m.Content |> Seq.map _.Text
        | _ -> []

    let toReasoningOutput(ri:Responses.ResponseItem) =
        match ri with 
        | :? Responses.ReasoningResponseItem as m -> m.SummaryParts |> Seq.map (function :? Responses.ReasoningSummaryTextPart as t -> t.Text | _ -> "")
        | _ -> []

    let rec internal callGenerateResponse attempts (respClient:Responses.OpenAIResponseClient) (items:Responses.ResponseItem seq) (opts:Responses.ResponseCreationOptions) = async {
        try 
            let! resp = respClient.CreateResponseAsync(items, options=opts) |> Async.AwaitTask
            let text = resp.Value.OutputItems |> Seq.collect toTextOutput |> String.concat ""
            let reasoning = resp.Value.OutputItems |> Seq.collect toReasoningOutput |> String.concat ""
            return text,reasoning            
        with ex -> 
            if attempts > 0 then 
                Log.warn $"callGenerateResponse failed, attempts remain {attempts - 1}. Error {ex.Message}"
                do! Async.Sleep 3000
                return! callGenerateResponse (attempts - 1) respClient items opts
            else
                Log.exn (ex,"callGenerateResponse")
                return raise ex
    }

    let internal generate attempts model (chat:ChatMessage seq) (outputFormat:JsonElement option) (gopts:GenOpts option) (endpoint:ApiEndpoint) = async {
        let copts = OpenAI.OpenAIClientOptions(Endpoint = Uri endpoint.ENDPOINT)
        let client = OpenAIClient(ClientModel.ApiKeyCredential(endpoint.API_KEY), copts)
        let respClient = client.GetOpenAIResponseClient(model)
        let responseItems : Responses.ResponseItem seq = chat |> Seq.map toResponsesItem
        let opts = Responses.ResponseCreationOptions()
        gopts
        |> Option.iter(fun o -> 
            o.temperature |> Option.iter (fun t -> opts.Temperature <- t)
            o.max_tokens |> Option.iter (fun t -> opts.MaxOutputTokenCount <- t))
        opts.ReasoningOptions <- Responses.ResponseReasoningOptions(
           ReasoningEffortLevel=Responses.ResponseReasoningEffortLevel.High,
            ReasoningSummaryVerbosity=Responses.ResponseReasoningSummaryVerbosity.Detailed)
        match outputFormat with 
        | Some fmt -> 
            let schemaJson = fmt.GetRawText()
            let schemaData = BinaryData(schemaJson)
            let txOpts = Responses.ResponseTextOptions()
            txOpts.TextFormat <- Responses.ResponseTextFormat.CreateJsonSchemaFormat("schema", schemaData, jsonSchemaIsStrict=true)
            opts.TextOptions <- txOpts
        | None -> ()
        return! callGenerateResponse 5 respClient responseItems opts
    }

module CompletionsApi = 
    open OpenAI
    open OpenAI.Chat
    open FSharp.Control
    open System.Text.Json
    open System
    open FsGepa

    let rec internal callGenerate attempts hasThought chat opts (client:ChatClient) = async {
        try 
            let thoughts = ref ""
            let output = 
                client.CompleteChatStreamingAsync(chat,opts)
                |> AsyncSeq.ofAsyncEnum
                //|> AsyncSeq.map(fun x -> printfn $"""c:{x.ContentUpdate.Count},{if x.ContentUpdate.Count > 0 then string x.ContentUpdate.[0].Text else ""}"""; x)
                |> AsyncSeq.filter(fun x -> x.ContentUpdate.Count > 0)
                |> AsyncSeq.map(fun x->x.ContentUpdate[0].Text)
    //          |> AsyncSeq.bufferByCountAndTime 1 C.CHAT_RESPONSE_TIMEOUT
    //          |> AsyncSeq.collect(fun xs -> if xs.Length > 0 then AsyncSeq.ofSeq xs else failwith C.TIMEOUT_MSG)
                |> AsyncSeq.bufferByCountAndTime 10 1000
                |> AsyncSeq.filter(fun xs -> xs.Length > 0)
                |> AsyncSeq.map(String.concat "")
                |> fun xs -> 
                    if hasThought then  
                        //----- stream parse think tokens  -----
                        xs
                        |> AsyncSeq.scan StreamParser.updateState (StreamParser.harmonyExp thoughts,(StreamParser.State.Empty,[]))
                        |> AsyncSeq.collect (fun (_,(_,os)) -> os |> List.rev |> AsyncSeq.ofSeq)
                        //-----------------------------
                    else
                        xs 
                |> AsyncSeq.toBlockingSeq
                |> String.concat ""
            return (output,thoughts.Value)
        with ex -> 
            if attempts > 0 then    
                Log.warn $"llm call failed attempts left {attempts - 1}: Error: {ex.Message}"
                do! Async.Sleep 3000
                return! callGenerate (attempts - 1) hasThought chat opts client
            else 
                Log.exn(ex,"callGenerate")
                return raise ex
    }

    let internal generate attempts model chat (outputFormat:JsonElement option) (gopts:GenOpts option) endpoint hasThought = async {
        let copts = OpenAI.OpenAIClientOptions(Endpoint = Uri endpoint.ENDPOINT)
        let client = ChatClient(model,ClientModel.ApiKeyCredential(endpoint.API_KEY),copts) //instead of semantic kernel we can use the native chat completions client to pass json 
        let opts = new ChatCompletionOptions()
        gopts
        |> Option.iter(fun o -> 
            o.temperature |> Option.iter (fun t -> opts.Temperature <- t)
            o.max_tokens |> Option.iter (fun t -> opts.MaxOutputTokenCount <- t))
        match outputFormat with 
        | Some fmt ->
            let schemaJson = fmt.GetRawText()
            let schemaData = BinaryData(schemaJson)
            opts.ResponseFormat <- ChatResponseFormat.CreateJsonSchemaFormat("schema", schemaData, jsonSchemaIsStrict=true)
        | None -> ()
        return! callGenerate attempts hasThought chat opts client
    }

module GenAI =
    open OpenAI.Chat
    open FSharp.Control
    open System.Text.Json
    open System
    open FsGepa

    let internal generate (backend:Backend) (systemMessage:string option) (msgs:GenMessage list) (outputFormat:JsonElement option) (opts:GenOpts option) (model:Model) = async {
        let chat = 
            seq {
                match systemMessage with 
                | Some sm -> SystemChatMessage(sm) :> ChatMessage
                | None -> for m in msgs do
                            match m.role with 
                            | "user" | "User" | "USER"  -> UserChatMessage(m.content) :> ChatMessage
                            | "assistant" | "Assistant" | "ASSISTANT" -> AssistantChatMessage(m.content) :> ChatMessage
                            | _ -> failwithf "Unknown role %s" m.role
            }

        match backend.backendType with 
        | BackendType.Responses -> return! ResponsesApi.generate 5 model.id chat outputFormat opts backend.endpoint
        | BackendType.ChatCompletions -> return! CompletionsApi.generate 5 model.id chat outputFormat opts backend.endpoint false
        | BackendType.ChatCompletionsHarmony -> return! CompletionsApi.generate 5 model.id chat outputFormat opts backend.endpoint true
        
    }

    let createDefault  backend =
        {new IGenerate with         
            member this.generate(model: Model) (systemMessage: string option) (messages: GenMessage list) (responseFormat: Type option) (opts:GenOpts option): Async<GenerateResponse> = async {
                let responseFormat = responseFormat |> Option.map Schema.generate
                let! output,thoughts = generate backend systemMessage messages responseFormat opts model
                return {output=output; thoughts=checkEmpty thoughts}
            }                
        }
        