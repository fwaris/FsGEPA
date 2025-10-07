namespace FsgGenAI

type BackendType = ChatCompletions | ChatCompletionsHarmony


type ApiEndpoint =
    {
        API_KEY : string
        ENDPOINT : string
    }

type Backend = {
    model : string
    endpoint : ApiEndpoint
    backendType : BackendType
}

module GenAI =
    open OpenAI.Chat
    open FSharp.Control
    open System.Text.Json
    open System

    let (===) (a:string) (b:string) = a.Equals(b, StringComparison.CurrentCultureIgnoreCase)

    let generate (backend:Backend,systemMessage:string option,prompt:string, outputFormat:JsonElement option) = task {

        let copts = OpenAI.OpenAIClientOptions(Endpoint = Uri backend.endpoint.ENDPOINT)
        let client = ChatClient(backend.model,ClientModel.ApiKeyCredential(backend.endpoint.API_KEY),copts) //instead of semantic kernel we can use the native chat completions client to pass json schema

        let chat = 
            seq {
                match systemMessage with 
                | Some sm -> SystemChatMessage(sm) :> ChatMessage
                | None -> ()
                UserChatMessage(prompt)
            }

        let opts = new ChatCompletionOptions()

        match outputFormat with 
        | Some fmt ->
            let schemaJson = fmt.GetRawText()
            let schemaData = BinaryData(schemaJson)
            opts.ResponseFormat <- ChatResponseFormat.CreateJsonSchemaFormat("schema", schemaData)
        | None -> ()

        let thoughts = ref ""
        let hasThought = backend.backendType.IsChatCompletionsHarmony

        let texts = 
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
        return (thoughts.Value,texts)
    }
