namespace FsgSample.Fvr
open System
open System.IO
open FsGepa
open Microsoft.Data.Sqlite
open System.Text.Json
open System.Text.Json.Serialization
open FParsec
open System.Text.RegularExpressions

type Content = {
    [<JsonPropertyName("content")>]
    content : string list
    [<JsonPropertyName("context")>]
    context : Map<string,string list>
}

type FeverousRecord = {
    [<JsonPropertyName("id")>]
    id : int
    [<JsonPropertyName("claim")>]
    claim : string 
    [<JsonPropertyName("label")>]
    label : string
    [<JsonPropertyName("evidence")>]
    evidence : Content list
    [<JsonPropertyName("challenge")>]
    challenge : string
}

type WRef = WRef of {|original:string; t:WRefT|}
and WRefT = 
    | Sentence of {|title:string; sentence_id:int;|}
    | Cell of {|title:string; table:int; row:int; col:int; |}
    | HeaderCell of {|title:string; table:int; row:int; col:int; |}
    | Title of {|title:string;|}
    | Caption of {|title:string; table:int|}
    | Section of {|title:string; section:int|}
    | Item of {|title:string; list_id:int; item_id:int|}

module WRef = 
    let inline comb a b = a,b
    let p_sentence_end : Parser<_,unit> = pstring "_sentence_" >>. pint32 
    let p_sentence = many1CharsTillApply anyChar p_sentence_end comb  
                     |>> fun (a,b) -> Sentence {|title=a; sentence_id=b|}

    let p_header_cell_end : Parser<_,unit> = 
        pstring "_header_cell_" 
        >>. pint32 .>> pchar '_' 
        .>>. pint32 .>>  pchar '_'
        .>>. pint32 
    let p_header_cell = many1CharsTillApply anyChar p_header_cell_end comb
                        |>> fun (t,((tbl,r),c)) -> HeaderCell {|title=t; table=tbl;row=r;col=c|}

    let p_cell_end : Parser<_,unit> = 
        pstring "_cell_" 
        >>. pint32 .>> pchar '_' 
        .>>. pint32 .>>  pchar '_'
        .>>. pint32 
    let p_cell = many1CharsTillApply anyChar p_cell_end comb
                 |>> fun (t,((tbl,r),c)) -> Cell {|title=t; table=tbl;row=r;col=c|}

    let p_section_end : Parser<_,unit> = pstring "_section_" >>. pint32
    let p_section = many1CharsTillApply anyChar p_section_end comb
                    |>> fun (t,s) -> Section {|title=t; section=s|}

    let p_title : Parser<_,unit> = many1CharsTill anyChar (pstring "_title")
                                   |>> fun t -> Title {|title=t|}

    let p_table_caption_end : Parser<_,unit> = pstring "_table_caption_" >>. pint32
    let p_table_caption = many1CharsTillApply anyChar p_table_caption_end comb 
                          |>> fun (t,tbl) -> Caption {|title=t; table=tbl|}

    let p_item_end : Parser<_,unit> = pstring "_item_" >>. pint32 .>> pchar '_' .>>. pint32
    let p_item = many1CharsTillApply anyChar p_item_end comb 
                 |>> fun (t,(x,y)) -> Item {|title=t; list_id=x; item_id=y|}

    let p_all = choice [
                    attempt p_header_cell
                    attempt p_cell
                    attempt p_section
                    attempt p_table_caption
                    attempt p_title
                    attempt p_item
                    p_sentence
                ] .>> eof
    let tryParse s = match run p_all s with Success (r,_,_) -> WRef {|original=s; t=r|} |> Some | _ -> None


type FeverousResolved = {
    id : int
    claim : string
    label : string
    document : string
}


module Data = 
    let FEVEROUS = home @@ "Downloads" @@ "feverous_dev_challenges.jsonl"
    let FEVEROUS_DB = home @@ "Downloads" @@ "feverous_wikiv1.db"

    let wikiLinkRegex = Regex(@"\[\[([^\]\|]+)(\|([^\]]+))?\]\]", RegexOptions.Compiled)

    let convertWikiLinksToMarkdown (text: string) =
        wikiLinkRegex.Replace(text, fun m ->
            let linkTarget = m.Groups.[1].Value.Trim().Replace(" ", "_")
            let linkText = if m.Groups.[3].Success then m.Groups.[3].Value.Trim() else m.Groups.[1].Value.Trim()
            $"[{linkText}]({linkTarget})"
        )

    let jsonOptions = 
        let options = JsonSerializerOptions()
        options.PropertyNameCaseInsensitive <- true
        options

    let loadFeverous (path: string) : FeverousRecord[] =
        if File.Exists path then
            File.ReadLines path
            |> Seq.skip 1
            |> Seq.filter (String.IsNullOrWhiteSpace >> not)
            |> Seq.map (fun line ->
                try
                    JsonSerializer.Deserialize<FeverousRecord>(line, jsonOptions)
                with ex ->
                    printfn "Failed to parse line: %s" line
                    reraise ()
            )
            |> Seq.toArray
        else
            [||]

    let fetchPageJson (pageTitle: string) =
        if File.Exists FEVEROUS_DB then
            use conn = new SqliteConnection($"Data Source={FEVEROUS_DB};Mode=ReadOnly")
            conn.Open()

            use cmd = conn.CreateCommand()
            cmd.CommandText <- "SELECT data FROM wiki WHERE id = $id"
            cmd.Parameters.AddWithValue("$id", pageTitle) |> ignore

            match cmd.ExecuteScalar() with
            | :? string as json -> Some json
            | _ -> None
        else
            None

    let getTitle = function 
        | Title t -> Some t.title 
        | Section s -> Some s.title 
        | Caption c -> Some c.title
        | Sentence s -> Some s.title
        | Cell c -> Some c.title
        | HeaderCell h -> Some h.title
        | Item i -> Some i.title

    let extractPageTitles (fr: FeverousRecord) =
        fr.evidence
        |> List.collect _.content
        |> List.choose WRef.tryParse
        |> List.choose (fun (WRef wr) -> getTitle wr.t)
        |> List.distinct

    // Helper function to get content from JSON based on reference type
    let fetchContent (wref: WRef) : string option =
        let (WRef wr) = wref
        match fetchPageJson (getTitle wr.t |> Option.defaultValue "") with
        | None -> None
        | Some json ->
            try
                use doc = JsonDocument.Parse(json)
                match wr.t with
                | Sentence s ->
                    let key = $"sentence_{s.sentence_id}"
                    let mutable prop = JsonElement()
                    if doc.RootElement.TryGetProperty(key, &prop) then
                        Some (prop.GetString())
                    else None
                | Section sec ->
                    let key = $"section_{sec.section}"
                    let mutable prop = JsonElement()
                    if doc.RootElement.TryGetProperty(key, &prop) then
                        Some (prop.GetString())
                    else None
                | Caption c ->
                    let tableKey = $"table_{c.table}"
                    let mutable tableProp = JsonElement()
                    if doc.RootElement.TryGetProperty(tableKey, &tableProp) then
                        let mutable captionProp = JsonElement()
                        if tableProp.TryGetProperty("caption", &captionProp) then
                            Some (captionProp.GetString())
                        else None
                    else None
                | Title t ->
                    let mutable prop = JsonElement()
                    if doc.RootElement.TryGetProperty("title", &prop) then
                        Some (prop.GetString())
                    else None
                | Item i ->
                    let key = $"item_{i.list_id}_{i.item_id}"
                    let mutable prop = JsonElement()
                    if doc.RootElement.TryGetProperty(key, &prop) then
                        Some (prop.GetString())
                    else None
                | Cell _ | HeaderCell _ -> None // Handled separately in table rendering
            with _ -> None

    // Helper function to render a table with highlighted cells
    let renderTableWithHighlights (pageTitle: string) (tableId: int) (highlightCells: Set<int*int>) : string option =
        match fetchPageJson pageTitle with
        | None -> None
        | Some json ->
            try
                use doc = JsonDocument.Parse(json)
                let tableKey = $"table_{tableId}"
                let mutable tableProp = JsonElement()
                if doc.RootElement.TryGetProperty(tableKey, &tableProp) then
                    let table = tableProp.GetProperty("table")
                    let rows = table.EnumerateArray() |> Seq.toArray
                    
                    // Get caption if exists
                    let caption = 
                        let mutable captionProp = JsonElement()
                        if tableProp.TryGetProperty("caption", &captionProp) then
                            Some (captionProp.GetString())
                        else None
                    
                    // Build markdown table
                    let sb = System.Text.StringBuilder()
                    
                    // Add caption as first row if it exists
                    caption |> Option.iter (fun c ->
                        // Determine column count from first data row
                        let colCount = if rows.Length > 0 then rows.[0].EnumerateArray() |> Seq.length else 1
                        sb.Append($"| **{c}** ") |> ignore
                        for _ = 1 to colCount - 1 do
                            sb.Append("| ") |> ignore
                        sb.AppendLine("|") |> ignore
                        for _ = 0 to colCount - 1 do
                            sb.Append("| --- ") |> ignore
                        sb.AppendLine("|") |> ignore
                    )
                    
                    // Process each row
                    for rowIdx = 0 to rows.Length - 1 do
                        let row = rows.[rowIdx]
                        let cells = row.EnumerateArray() |> Seq.toArray
                        
                        for colIdx = 0 to cells.Length - 1 do
                            let cell = cells.[colIdx]
                            let mutable valueProp = JsonElement()
                            let cellValue = 
                                if cell.TryGetProperty("value", &valueProp) then
                                    let rawValue = valueProp.GetString().Replace("\n", " ")
                                    convertWikiLinksToMarkdown rawValue
                                else ""
                            
                            // Highlight if this cell is in the highlight set
                            let displayValue = 
                                if highlightCells.Contains(rowIdx, colIdx) then
                                    $"**{cellValue}**"
                                else
                                    cellValue
                            
                            sb.Append($"| {displayValue} ") |> ignore
                        
                        sb.AppendLine("|") |> ignore
                        
                        // Add separator after first row (header row) - only if no caption was added
                        if rowIdx = 0 && caption.IsNone then
                            for _ = 0 to cells.Length - 1 do
                                sb.Append("| --- ") |> ignore
                            sb.AppendLine("|") |> ignore
                    
                    Some (sb.ToString())
                else
                    None
            with _ -> None

    let generateDoc (fr: FeverousRecord) : string =
        // Parse all content references
        let parsedRefs = 
            fr.evidence
            |> List.collect _.content
            |> List.choose (fun ref -> 
                WRef.tryParse ref
                |> Option.map (fun wr -> ref, wr))
        
        // Build context map from all evidence
        let contextMap = 
            fr.evidence
            |> List.collect (fun ev -> ev.context |> Map.toList)
            |> Map.ofList
        
        // Group references by page title and type
        let groupedByTitle = 
            parsedRefs
            |> List.groupBy (fun (_, WRef wr) -> getTitle wr.t |> Option.defaultValue "Unknown")
        
        // Build document sections
        let sections = 
            groupedByTitle
            |> List.map (fun (pageTitle, refs) ->
                let sb = System.Text.StringBuilder()
                sb.AppendLine($"## {pageTitle}") |> ignore
                sb.AppendLine() |> ignore
                
                // Separate tables from text content
                let tableCells = 
                    refs
                    |> List.choose (fun (_, WRef wr) ->
                        match wr.t with
                        | Cell c -> Some (c.table, (c.row, c.col))
                        | HeaderCell h -> Some (h.table, (h.row, h.col))
                        | _ -> None)
                    |> List.groupBy fst
                    |> List.map (fun (tableId, cells) -> 
                        tableId, cells |> List.map snd |> Set.ofList)
                
                let textRefs = 
                    refs
                    |> List.choose (fun (origRef, WRef wr) ->
                        match wr.t with
                        | Cell _ | HeaderCell _ -> None
                        | _ -> 
                            let context = 
                                contextMap 
                                |> Map.tryFind origRef 
                                |> Option.defaultValue []
                                |> List.choose WRef.tryParse
                                |> List.choose (fun (WRef wr2) -> 
                                    match wr2.t with
                                    | Section s -> Some s.section
                                    | _ -> None)
                                |> List.tryHead
                            Some (WRef wr, context))
                    |> List.groupBy snd
                
                // Render text content by section
                textRefs
                |> List.sortBy fst
                |> List.iter (fun (sectionId, items) ->
                    sectionId |> Option.iter (fun sid -> 
                        sb.AppendLine($"### Section {sid}") |> ignore
                        sb.AppendLine() |> ignore)
                    
                    items
                    |> List.choose (fun (wref, _) -> 
                        fetchContent wref
                        |> Option.map (fun content -> 
                            let (WRef wr) = wref
                            wr.t, convertWikiLinksToMarkdown content))
                    |> List.iter (fun (refType, content) ->
                        let prefix = 
                            match refType with
                            | Title _ -> "**Title:** "
                            | Sentence s -> $"**Sentence {s.sentence_id}:** "
                            | Caption c -> $"**Table {c.table} Caption:** "
                            | Item i -> $"**Item {i.list_id}-{i.item_id}:** "
                            | _ -> ""
                        sb.AppendLine($"{prefix}{content}") |> ignore
                        sb.AppendLine() |> ignore)
                )
                
                // Render tables
                if not tableCells.IsEmpty then
                    tableCells
                    |> List.sortBy fst
                    |> List.iter (fun (tableId, cells) ->
                        match renderTableWithHighlights pageTitle tableId cells with
                        | Some tableMarkdown ->
                            sb.AppendLine() |> ignore
                            sb.AppendLine(tableMarkdown) |> ignore
                        | None -> ())
                
                sb.ToString()
            )
        
        // Combine all sections
        let document = String.concat "\n\n---\n\n" sections
        
        // Return document without claim
        document
