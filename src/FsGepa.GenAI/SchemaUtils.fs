namespace FsGepa.GenAI

module SchemaUtils =
    open System
    open Microsoft.Extensions.AI
    open System.Text.Json
    open System.Text.Json.Nodes
    open System.Text.Json.Serialization
    open System.Reflection
    open System.ComponentModel

    let enumDescriptionTransform =
        Func<AIJsonSchemaCreateContext, JsonNode, JsonNode>(fun ctx node ->
            let enumType = ctx.TypeInfo.Type
            match node with
            | :? JsonObject as schema when enumType.IsEnum ->
                let hasEnumValues, enumNode = schema.TryGetPropertyValue("enum")
                if hasEnumValues then
                    match enumNode with
                    | :? JsonArray as enumValues when enumValues.Count > 0 ->
                        let descriptionLookup =
                            enumType.GetFields(BindingFlags.Public ||| BindingFlags.Static)
                            |> Array.choose (fun field ->
                                if field.IsSpecialName then None
                                else
                                    match field.GetCustomAttribute<DescriptionAttribute>() with
                                    | null -> None
                                    | attr when String.IsNullOrWhiteSpace attr.Description -> None
                                    | attr -> Some(field.Name, attr.Description))
                            |> Map.ofArray

                        if Map.count descriptionLookup = enumValues.Count then
                            let enumNames =
                                Array.init enumValues.Count (fun idx -> enumValues[idx].GetValue<string>())

                            if enumNames |> Array.forall (fun name -> Map.containsKey name descriptionLookup) then
                                let descArray = JsonArray()
                                enumNames
                                |> Array.iter (fun name -> descArray.Add(JsonValue.Create(descriptionLookup.[name])))
                                schema["enumDescriptions"] <- descArray
                    | _ -> ()
            | _ -> ()
            node)


    let schemaCreateOptions =
        AIJsonSchemaCreateOptions(
            TransformSchemaNode = enumDescriptionTransform,
            TransformOptions = AIJsonSchemaTransformOptions(DisallowAdditionalProperties = true))

    let private serializerOptions =
        let opts = JsonSerializerOptions(AIJsonUtilities.DefaultOptions)

        let fsharpConverterTypeName = "System.Text.Json.Serialization.Converters.FSharpTypeConverterFactory, System.Text.Json"
        let fsharpConverterType = Type.GetType(fsharpConverterTypeName)

        match fsharpConverterType with
        | null -> ()
        | converterType ->
            let alreadyAdded =
                opts.Converters
                |> Seq.exists (fun c -> converterType.IsAssignableFrom(c.GetType()))

            if not alreadyAdded then
                let instance = Activator.CreateInstance(converterType)
                match instance with
                | :? JsonConverter as converter -> opts.Converters.Add(converter)
                | _ -> ()

        let hasStringEnumConverter =
            opts.Converters
            |> Seq.exists (fun c -> c :? JsonStringEnumConverter)

        if not hasStringEnumConverter then
            opts.Converters.Add(JsonStringEnumConverter())

        opts

    let toSchema (t:Type) = AIJsonUtilities.CreateJsonSchema(
            t,
            description = t.Name,
            serializerOptions = serializerOptions,
            inferenceOptions = schemaCreateOptions
        )
        
