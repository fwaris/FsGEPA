#r "nuget: Microsoft.Data.Sqlite"
#r "nuget: FSharp.SystemTextJson, 1.4.36"

open System
open System.IO
open Microsoft.Data.Sqlite
open System.Text.Json

// Simple inline definitions instead of loading Utils.fs
let home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
let (@@) path1 path2 = Path.Combine(path1, path2)

type WRef = 
    | Sentence of {|title:string; sentence_id:string; original:string|}
    | Cell of {|title:string; table:int; row:int; col:int; original:string|}

let FEVEROUS_DB = home @@ "Downloads" @@ "feverous_wikiv1.db"

let tryParseWikiReference (reference: string) : WRef option =
    let markers = [ "_sentence_"; "_cell_"; "_header_cell_" ]
    let marker = markers |> List.tryFind (fun m -> reference.Contains(m))

    match marker with
    | Some marker ->
        let idx = reference.LastIndexOf(marker, StringComparison.Ordinal)
        if idx > 0 then
            let page = reference.Substring(0, idx)
            let key = reference.Substring(idx + 1)
            if not (String.IsNullOrWhiteSpace(page)) && not (String.IsNullOrWhiteSpace(key)) then
                if key.StartsWith("sentence_") then
                    Some (Sentence {|title=page; sentence_id=key; original=reference|})
                elif key.StartsWith("cell_") || key.StartsWith("header_cell_") then
                    let parts = key.Split('_') |> Array.toList
                    match parts with
                    | "cell" :: tableStr :: rowStr :: colStr :: _ 
                    | "header" :: "cell" :: tableStr :: rowStr :: colStr :: _ ->
                        match Int32.TryParse(tableStr), Int32.TryParse(rowStr), Int32.TryParse(colStr) with
                        | (true, table), (true, row), (true, col) ->
                            Some (Cell {|title=page; table=table; row=row; col=col; original=reference|})
                        | _ -> None
                    | _ -> None
                else
                    None
            else
                None
        else
            None
    | None -> None

let fetchPageJson id =
    if File.Exists FEVEROUS_DB then
        use conn = new SqliteConnection($"Data Source={FEVEROUS_DB};Mode=ReadOnly")
        conn.Open()

        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT data FROM wiki WHERE id = $id"
        cmd.Parameters.AddWithValue("$id", id) |> ignore

        match cmd.ExecuteScalar() with
        | :? string as json -> Some json
        | _ -> None
    else
        None

// Test the parsing with the problematic reference
let testRef = "Aramais Yepiskoposyan_cell_0_6_1"
printfn "Testing reference: %s" testRef

let parsed = tryParseWikiReference testRef

match parsed with
| Some (Cell cell) -> 
    printfn "\nSuccessfully parsed as Cell:"
    printfn "  title: %s" cell.title
    printfn "  table: %d" cell.table
    printfn "  row: %d" cell.row
    printfn "  col: %d" cell.col
    printfn "  original: %s" cell.original
    
    // Test key extraction
    let key = cell.original.Substring(cell.title.Length + 1)
    printfn "  extracted key: %s" key
    
| Some (Sentence s) -> 
    printfn "\nParsed as Sentence (unexpected):"
    printfn "  title: %s" s.title
    printfn "  sentence_id: %s" s.sentence_id
    
| None -> 
    printfn "\nFailed to parse!"

// Test fetching the actual text (if database exists)
printfn "\nAttempting to fetch text from database..."
match parsed with
| Some (Cell cell) ->
    let key = cell.original.Substring(cell.title.Length + 1)
    match fetchPageJson cell.title with
    | Some json ->
        printfn "Found page JSON, length: %d" json.Length
        printfn "Key to search for: %s" key
        
        // Parse and examine the JSON structure
        use doc = JsonDocument.Parse(json)
        printfn "\nTop-level properties in JSON:"
        for prop in doc.RootElement.EnumerateObject() do
            printfn "  - %s" prop.Name
            
        // Look for table_0 specifically
        let mutable tableProp = JsonElement()
        if doc.RootElement.TryGetProperty("table_0", &tableProp) then
            printfn "\nFound table_0, examining structure..."
            let table = tableProp.GetProperty("table")
            printfn "Table has %d rows" (table.GetArrayLength())
            
            // Look at row 6 column 1
            let rows = table.EnumerateArray() |> Seq.toArray
            if rows.Length > 6 then
                let row6 = rows.[6]
                let cells = row6.EnumerateArray() |> Seq.toArray
                if cells.Length > 1 then
                    let cell6_1 = cells.[1]
                    printfn "\nCell at row 6, col 1:"
                    printfn "  Raw JSON: %s" (cell6_1.GetRawText())
                    let mutable idProp = JsonElement()
                    if cell6_1.TryGetProperty("id", &idProp) then
                        printfn "  ID: %s" (idProp.GetString())
                    let mutable valueProp = JsonElement()
                    if cell6_1.TryGetProperty("value", &valueProp) then
                        printfn "  Value: %s" (valueProp.GetString())
        else
            printfn "\nNo table_0 found in JSON"
            
    | None ->
        printfn "Page not found in database"
| Some (Sentence sentence) ->
    match fetchPageJson sentence.title with
    | Some json ->
        printfn "Found page JSON for sentence"
    | None ->
        printfn "Page not found in database"
| None -> 
    printfn "Cannot fetch - parsing failed"
