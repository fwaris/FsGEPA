namespace FsGepa
open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization

[<AutoOpen>]
module Utils =
    open System.Text.Encodings.Web

    let home = Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)

    let inline debug (s:'a) = System.Diagnostics.Debug.WriteLine(s)
    
    let (===) (a:string) (b:string) = a.Equals(b,StringComparison.CurrentCultureIgnoreCase)
    
    let (====) (a:string option) (b:string option) =
        match a,b with
        | Some a, Some b -> a === b
        | None,None      -> true
        | _              -> false
    
    let newId() = 
        Guid.NewGuid().ToByteArray() 
        |> Convert.ToBase64String 
        |> Seq.takeWhile (fun c -> c <> '=') 
        |> Seq.map (function '/' -> 'a' | c -> c)
        |> Seq.toArray 
        |> String

    let notEmpty (s:string) = String.IsNullOrWhiteSpace s |> not
    let isEmpty (s:string) = String.IsNullOrWhiteSpace s 
    let contains (s:string) (ptrn:string) = s.Contains(ptrn,StringComparison.CurrentCultureIgnoreCase)
    let checkEmpty (s:string) = if isEmpty s then None else Some s

    let shorten len (s:string) = if s.Length > len then s.Substring(0,len) + "..." else s

    let (@@) a b = System.IO.Path.Combine(a,b)

    let rng = new Random()  
    let randSelect (ls:_ list) = if ls.IsEmpty then failwith "empty list" else ls.[rng.Next(ls.Length)]

    let extractTripleQuoted (inp:string) =
        let lines =
            seq {
                use sr = new System.IO.StringReader(inp)
                let mutable line = sr.ReadLine()
                while line <> null do
                    yield line
                    line <- sr.ReadLine()
            }
            |> Seq.map(fun x -> x)
            |> Seq.toList
        let addSnip acc accSnip =
            match accSnip with
            |[] -> acc
            | _ -> (List.rev accSnip)::acc
        let isQuote (s:string) = s.StartsWith("```")
        let rec start acc (xs:string list) =
            match xs with
            | []                   -> List.rev acc
            | x::xs when isQuote x -> accQuoted acc [] xs
            | x::xs                -> start acc xs
        and accQuoted acc accSnip xs =
            match xs with
            | []                   -> List.rev (addSnip acc accSnip)
            | x::xs when isQuote x -> start (addSnip acc accSnip) xs
            | x::xs                -> accQuoted acc (x::accSnip) xs
        start [] lines

    let extractQuoted inp =
        let outstr = 
            extractTripleQuoted inp
            |> Seq.collect id
            |> fun xs -> String.Join("\n",xs)
        if outstr.EndsWith("```") then 
            outstr.Remove(outstr.Length-3)

        else 
            outstr
            
    let serOptionsFSharp = lazy(
        let o = JsonSerializerOptions(JsonSerializerDefaults.General)
        o.WriteIndented <- true
        o.ReadCommentHandling <- JsonCommentHandling.Skip        
        let opts = JsonFSharpOptions.Default()
        opts
            .WithSkippableOptionFields(true)
            .WithAllowNullFields(true)
            .AddToJsonSerializerOptions(o)        
        o)
        
    ///<summary>
    ///Json serialization options suitable for deserializing OpenAI 'structured output'.<br />
    ///Note: can use simple enums, in such types but not F# DUs
    ///</summary>
    /// 
    let openAIResponseSerOpts =
        let o = JsonSerializerOptions(JsonSerializerDefaults.General)
        o.Converters.Add(JsonStringEnumConverter())
        o.WriteIndented <- true
        o.Encoder <- JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        o.ReadCommentHandling <- JsonCommentHandling.Skip
        let opts = JsonFSharpOptions.Default()
        opts
            .WithSkippableOptionFields(true)
            .AddToJsonSerializerOptions(o)
        o

    ///Serialize object to json with minimal escaping
    let formatJson<'t>(j:'t) =
        JsonSerializer.Serialize(j,openAIResponseSerOpts)