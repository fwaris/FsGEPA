namespace FsgGenAI

/// <summary>
/// Use ChatCompletionsHarmony if the `model + inference server` returns think tokens in harmony format.<br />
/// For example, `gpt-oss-20b + llama.cpp` returns thought stream in the main output as:<br />
/// `&lt;|channel|&gt;analysis&lt;|message|&gt;  ...thoughts...  &lt;|end|&gt; ...actual output...`<br />
/// </summary>
[<RequireQualifiedAccess>]
type BackendType = 
    | ChatCompletions 
    | ChatCompletionsHarmony

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

module GenAI =
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
            return (thoughts.Value,output)
        with ex -> 
            if attempts > 0 then    
                Log.warn $"llm call failed attempts left {attempts - 1}: Error: {ex.Message}"
                do! Async.Sleep 3000
                return! callGenerate (attempts - 1) hasThought chat opts client
            else 
                Log.exn(ex,"callGenerate")
                return raise ex
    }

    let internal generate (backend:Backend) (systemMessage:string option) (msgs:GenMessage list) (outputFormat:JsonElement option) (model:Model) = async {

        let copts = OpenAI.OpenAIClientOptions(Endpoint = Uri backend.endpoint.ENDPOINT)
        let client = ChatClient(model.id,ClientModel.ApiKeyCredential(backend.endpoint.API_KEY),copts) //instead of semantic kernel we can use the native chat completions client to pass json schema

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

        let opts = new ChatCompletionOptions()

        match outputFormat with 
        | Some fmt ->
            let schemaJson = fmt.GetRawText()
            let schemaData = BinaryData(schemaJson)
            opts.ResponseFormat <- ChatResponseFormat.CreateJsonSchemaFormat("schema", schemaData)
        | None -> ()

        let hasThought = backend.backendType.IsChatCompletionsHarmony
        let! output,thoughts = callGenerate 5 hasThought chat opts client
        return output, thoughts
    }

    let createDefault  backend =
        {new IGenerate with         
            member this.generate(model: Model) (systemMessage: string option) (messages: GenMessage list) (responseFormat: Type option): Async<GenerateResponse> = async {
                let responseFormat = responseFormat |> Option.map Schema.generate
                let! output,thoughts = generate backend systemMessage messages responseFormat model
                return {output=output; thoughts=checkEmpty thoughts}
            }                
        }
        