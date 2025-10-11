#r "nuget:DuckDB.NET.Data,1.1.3"
#r "nuget:DuckDB.NET.Bindings.Full,1.1.3"

open System
open System.Collections
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.RegularExpressions
open DuckDB.NET.Data

type ColumnInfo = {
    Name: string
    DataType: string
    IsNullable: string
    Key: string
    Default: string
    Extra: string
}

type ColumnStats = {
    Column: string
    NonNullRows: int64
    NullRows: int64
    ApproxDistinct: int64
}

type ChatMessage = {
    Content: string
    Role: string
}

type GroundTruthValue =
    | GTNull
    | GTBool of bool
    | GTString of string
    | GTInteger of int64
    | GTFloating of decimal
    | GTArray of GroundTruthValue list
    | GTObject of Map<string, GroundTruthValue>

type GroundTruthInstruction = {
    Id: string
    Arguments: GroundTruthValue
}

type IfbenchRow = {
    Key: string
    Messages: ChatMessage list
    GroundTruth: GroundTruthInstruction list
    GroundTruthRaw: string
    Dataset: string
    ConstraintType: string
    Constraint: string
}

let parquetPath = "/Users/Faisal.Waris1/Downloads/ifbench_rlvr.parquet"
let jsonOutputPath = Path.ChangeExtension(parquetPath, ".json")
let sampleRowCount = 5
let sampleValueCount = 5

let failIfMissing path =
    if not (File.Exists path) then
        failwithf "Parquet file not found: %s" path

let replicate (count:int) (value:string) =
    if count <= 0 then String.Empty
    else String.Concat(Array.init count (fun _ -> value))

let printDivider (title:string) =
    let line = replicate (max 0 (80 - title.Length - 2)) "-"
    let prefix = replicate 3 "-"
    printfn "\n%s %s %s" prefix title line

let formatBytes (bytes:int64) =
    let units = [| "B"; "KB"; "MB"; "GB"; "TB" |]
    let mutable size = float bytes
    let mutable unitIndex = 0
    while size >= 1024.0 && unitIndex < units.Length - 1 do
        size <- size / 1024.0
        unitIndex <- unitIndex + 1
    sprintf "%s %s" (size.ToString("0.###", CultureInfo.InvariantCulture)) units.[unitIndex]

let escapeLiteral (value:string) =
    value.Replace("'", "''")

let tryGetString (reader:DuckDBDataReader) index =
    if reader.IsDBNull index then "" else reader.GetString index

let toChatMessages (value:obj) =
    match value with
    | :? IEnumerable as source ->
        source
        |> Seq.cast<obj>
        |> Seq.choose (fun item ->
            match item with
            | :? IDictionary as dict ->
                let tryGetField (key:string) =
                    if dict.Contains key then
                        match dict.[key] with
                        | null -> ""
                        | field -> field.ToString()
                    else ""
                Some {
                    Content = tryGetField "content"
                    Role = tryGetField "role"
                }
            | _ -> None)
        |> Seq.toList
    | null -> []
    | other -> failwithf "Unsupported messages value type: %s" (other.GetType().FullName)

type private PyValue =
    | PyNull
    | PyBool of bool
    | PyString of string
    | PyInt of int64
    | PyFloat of float
    | PyList of PyValue list
    | PyDict of Map<string, PyValue>

let private skipWhitespace (text:string) (index:int) =
    let mutable i = index
    while i < text.Length && Char.IsWhiteSpace text.[i] do
        i <- i + 1
    i

let rec private parsePyValue (text:string) (index:int) : PyValue * int =
    let idx = skipWhitespace text index
    if idx >= text.Length then failwith "Unexpected end of ground truth payload."
    match text.[idx] with
    | '[' -> parsePyList text idx
    | '{' -> parsePyDict text idx
    | '\''
    | '"' ->
        let str, nextIndex = parsePyString text idx
        PyString str, nextIndex
    | '-' -> parsePyNumber text idx
    | c when Char.IsDigit c -> parsePyNumber text idx
    | 'N' -> parsePyNull text idx
    | 'T' -> parsePyTrue text idx
    | 'F' -> parsePyFalse text idx
    | unexpected -> failwithf "Unexpected character '%c' at position %d in ground truth payload." unexpected idx

and private parsePyList (text:string) (index:int) : PyValue * int =
    let rec loop i acc =
        let i = skipWhitespace text i
        if i >= text.Length then failwith "Unterminated list in ground truth payload."
        match text.[i] with
        | ']' -> List.rev acc, i + 1
        | _ ->
            let value, nextIndex = parsePyValue text i
            let nextIndex = skipWhitespace text nextIndex
            if nextIndex < text.Length && text.[nextIndex] = ',' then
                loop (nextIndex + 1) (value :: acc)
            elif nextIndex < text.Length && text.[nextIndex] = ']' then
                List.rev (value :: acc), nextIndex + 1
            else
                failwithf "Expected ',' or ']' at position %d in ground truth list." nextIndex
    let values, nextIndex = loop (index + 1) []
    PyList values, nextIndex

and private parsePyDict (text:string) (index:int) : PyValue * int =
    let rec loop i acc =
        let i = skipWhitespace text i
        if i >= text.Length then failwith "Unterminated dictionary in ground truth payload."
        match text.[i] with
        | '}' -> Map.ofList (List.rev acc), i + 1
        | _ ->
            let key, afterKey = parsePyString text i
            let afterColon = skipWhitespace text afterKey
            if afterColon >= text.Length || text.[afterColon] <> ':' then
                failwithf "Expected ':' after key '%s' at position %d in ground truth dictionary." key afterColon
            let value, afterValue = parsePyValue text (afterColon + 1)
            let afterValue = skipWhitespace text afterValue
            if afterValue < text.Length && text.[afterValue] = ',' then
                loop (afterValue + 1) ((key, value) :: acc)
            elif afterValue < text.Length && text.[afterValue] = '}' then
                Map.ofList (List.rev ((key, value) :: acc)), afterValue + 1
            else
                failwithf "Expected ',' or '}' at position %d in ground truth dictionary." afterValue
    let map, nextIndex = loop (index + 1) []
    PyDict map, nextIndex

and private parsePyNumber (text:string) (index:int) : PyValue * int =
    let mutable i = index
    let mutable isFloat = false
    if text.[i] = '-' then i <- i + 1
    if i >= text.Length || not (Char.IsDigit text.[i]) then
        failwithf "Invalid numeric literal at position %d." index
    while i < text.Length && Char.IsDigit text.[i] do
        i <- i + 1
    if i < text.Length && text.[i] = '.' then
        isFloat <- true
        i <- i + 1
        if i >= text.Length || not (Char.IsDigit text.[i]) then
            failwithf "Invalid fractional part in numeric literal at position %d." index
        while i < text.Length && Char.IsDigit text.[i] do
            i <- i + 1
    if i < text.Length && (text.[i] = 'e' || text.[i] = 'E') then
        isFloat <- true
        i <- i + 1
        if i < text.Length && (text.[i] = '+' || text.[i] = '-') then
            i <- i + 1
        if i >= text.Length || not (Char.IsDigit text.[i]) then
            failwithf "Invalid exponent in numeric literal at position %d." index
        while i < text.Length && Char.IsDigit text.[i] do
            i <- i + 1
    let literal = text.Substring(index, i - index)
    if isFloat then
        let value = Double.Parse(literal, CultureInfo.InvariantCulture)
        PyFloat value, i
    else
        let value = Int64.Parse(literal, CultureInfo.InvariantCulture)
        PyInt value, i

and private parsePyString (text:string) (index:int) : string * int =
    if index >= text.Length then
        failwithf "Unexpected end of payload when parsing string literal at position %d." index
    let quote = text.[index]
    if quote <> '\'' && quote <> '"' then
        failwithf "Expected quote at position %d when parsing string literal." index
    let sb = StringBuilder()
    let rec loop i =
        if i >= text.Length then failwith "Unterminated string literal in ground truth payload."
        match text.[i] with
        | '\\' when i + 1 < text.Length ->
            match text.[i + 1] with
            | '\\' -> sb.Append('\\') |> ignore; loop (i + 2)
            | '\'' -> sb.Append('\'') |> ignore; loop (i + 2)
            | '"' -> sb.Append('"') |> ignore; loop (i + 2)
            | 'n' -> sb.Append('\n') |> ignore; loop (i + 2)
            | 'r' -> sb.Append('\r') |> ignore; loop (i + 2)
            | 't' -> sb.Append('\t') |> ignore; loop (i + 2)
            | 'b' -> sb.Append('\b') |> ignore; loop (i + 2)
            | 'f' -> sb.Append('\f') |> ignore; loop (i + 2)
            | other -> sb.Append(other) |> ignore; loop (i + 2)
        | '\\' -> failwith "Unterminated escape sequence in ground truth string literal."
        | c when c = quote -> sb.ToString(), i + 1
        | c -> sb.Append(c) |> ignore; loop (i + 1)
    loop (index + 1)

and private parsePyNull (text:string) (index:int) : PyValue * int =
    if index + 4 <= text.Length && text.Substring(index, 4) = "None" then
        PyNull, index + 4
    else
        failwithf "Invalid token at position %d, expected None." index

and private parsePyTrue (text:string) (index:int) : PyValue * int =
    if index + 4 <= text.Length && text.Substring(index, 4) = "True" then
        PyBool true, index + 4
    else
        failwithf "Invalid token at position %d, expected True." index

and private parsePyFalse (text:string) (index:int) : PyValue * int =
    if index + 5 <= text.Length && text.Substring(index, 5) = "False" then
        PyBool false, index + 5
    else
        failwithf "Invalid token at position %d, expected False." index

let rec private toGroundTruthValue value =
    match value with
    | PyNull -> GTNull
    | PyBool b -> GTBool b
    | PyString s -> GTString s
    | PyInt i -> GTInteger i
    | PyFloat f -> GTFloating (decimal f)
    | PyList items -> GTArray (items |> List.map toGroundTruthValue)
    | PyDict dict ->
        dict
        |> Map.toSeq
        |> Seq.map (fun (k, v) -> k, toGroundTruthValue v)
        |> Map.ofSeq
        |> GTObject

let parseGroundTruth (raw:string) =
    if String.IsNullOrWhiteSpace raw then []
    else
        let payload = raw.Trim()
        let parsed, nextIndex = parsePyValue payload 0
        let finalIndex = skipWhitespace payload nextIndex
        if finalIndex <> payload.Length then
            failwith "Unexpected trailing characters in ground truth payload."
        match parsed with
        | PyList outer ->
            outer
            |> List.collect (fun entry ->
                match entry with
                | PyDict fields ->
                    let instructionIds =
                        match Map.tryFind "instruction_id" fields with
                        | Some (PyList ids) ->
                            ids
                            |> List.map (function
                                | PyString id -> id
                                | other -> failwithf "Instruction identifier must be a string but was %A." other)
                        | _ -> failwith "Ground truth entry missing instruction_id list."
                    let kwargs =
                        match Map.tryFind "kwargs" fields with
                        | Some (PyList items) -> items
                        | _ -> failwith "Ground truth entry missing kwargs list."
                    if instructionIds.Length <> kwargs.Length then
                        failwithf "Instruction/kwargs length mismatch (%d vs %d)." instructionIds.Length kwargs.Length
                    (instructionIds, kwargs)
                    ||> List.map2 (fun instruction kw ->
                        {
                            Id = instruction
                            Arguments = toGroundTruthValue kw
                        })
                | other -> failwithf "Unexpected entry in ground truth payload: %A" other)
        | other -> failwithf "Ground truth payload must be a list, but was %A." other

let rec formatGroundTruthValue value =
    match value with
    | GTNull -> "null"
    | GTBool b -> if b then "true" else "false"
    | GTString s ->
        let sanitized = s.Replace("\n", "\\n").Replace("\r", "\\r")
        sprintf "\"%s\"" sanitized
    | GTInteger i -> i.ToString(CultureInfo.InvariantCulture)
    | GTFloating f -> f.ToString(CultureInfo.InvariantCulture)
    | GTArray items ->
        items
        |> List.map formatGroundTruthValue
        |> fun parts -> String.Join(", ", parts)
        |> sprintf "[%s]"
    | GTObject map ->
        map
        |> Map.toSeq
        |> Seq.map (fun (k, v) -> sprintf "%s: %s" k (formatGroundTruthValue v))
        |> fun parts -> String.Join(", ", parts)
        |> sprintf "{%s}"

let private truncate (maxLength:int) (text:string) =
    if String.IsNullOrEmpty text || text.Length <= maxLength then text
    else text.Substring(0, maxLength) + "..."

let summarizeGroundTruthInstruction instruction =
    let argumentSummary =
        match instruction.Arguments with
        | GTNull -> None
        | GTObject map when Map.isEmpty map -> None
        | args -> formatGroundTruthValue args |> truncate 80 |> Some
    match argumentSummary with
    | Some summary -> sprintf "%s: %s" instruction.Id summary
    | None -> instruction.Id

let buildParquetSelect path limit =
    let baseQuery =
        $"""
        SELECT "key", "messages", "ground_truth", "dataset", "constraint_type", "constraint"
        FROM read_parquet('{escapeLiteral path}')
        """
    match limit with
    | Some l when l > 0 -> baseQuery + $" LIMIT {l}"
    | _ -> baseQuery

let parquetRowSeq (conn:DuckDBConnection) path limit : seq<IfbenchRow> =
    seq {
        use cmd = conn.CreateCommand()
        cmd.CommandText <- buildParquetSelect path limit
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            let getString idx = if reader.IsDBNull idx then "" else reader.GetString idx
            let messages = if reader.IsDBNull 1 then [] else toChatMessages (reader.GetValue 1)
            let key = getString 0
            let groundTruthRaw = getString 2
            let groundTruth =
                if String.IsNullOrWhiteSpace groundTruthRaw then []
                else
                    try parseGroundTruth groundTruthRaw
                    with ex ->
                        let preview = truncate 200 groundTruthRaw
                        failwithf "Failed to parse ground_truth for key '%s': %s. Preview: %s" key ex.Message preview
            yield {
                Key = key
                Messages = messages
                GroundTruth = groundTruth
                GroundTruthRaw = groundTruthRaw
                Dataset = getString 3
                ConstraintType = getString 4
                Constraint = getString 5
            }
    }

let loadParquetRows (conn:DuckDBConnection) path limit =
    parquetRowSeq conn path limit |> Seq.toList

let groundTruthInstructionStats (conn:DuckDBConnection) path =
    let counts = Dictionary<string, int>()
    parquetRowSeq conn path None
    |> Seq.iter (fun row ->
        row.GroundTruth
        |> List.iter (fun instruction ->
            let mutable current = 0
            if counts.TryGetValue(instruction.Id, &current) then
                counts[instruction.Id] <- current + 1
            else
                counts[instruction.Id] <- 1))
    counts
    |> Seq.map (fun kvp -> kvp.Key, kvp.Value)
    |> Seq.sortByDescending (fun (_, count) -> count)
    |> Seq.toList

let readSchema (conn:DuckDBConnection) path =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"DESCRIBE SELECT * FROM read_parquet('{escapeLiteral path}')"
    use reader = cmd.ExecuteReader()
    let items = ResizeArray<_>()
    while reader.Read() do
        items.Add {
            Name = tryGetString reader 0
            DataType = tryGetString reader 1
            IsNullable = tryGetString reader 2
            Key = tryGetString reader 3
            Default = tryGetString reader 4
            Extra = tryGetString reader 5
        }
    items |> Seq.filter (fun col -> not (String.IsNullOrWhiteSpace col.Name)) |> Seq.toList

let readRowCount (conn:DuckDBConnection) path =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"SELECT COUNT(*) FROM read_parquet('{escapeLiteral path}')"
    let result = cmd.ExecuteScalar()
    match result with
    | :? int64 as count -> count
    | :? int as count -> int64 count
    | _ -> failwith "Unexpected result type for row count"

let renderTable (reader:DuckDBDataReader) =
    let fieldCount = reader.FieldCount
    let headers = [| for i in 0 .. fieldCount - 1 -> reader.GetName i |]
    printfn "%s" (String.Join(" | ", headers))
    let dividerWidth = headers |> Array.sumBy (fun h -> h.Length + 3) |> max 1
    printfn "%s" (replicate dividerWidth "-")
    let mutable rowIndex = 0
    while reader.Read() do
        let values =
            [|
                for i in 0 .. fieldCount - 1 ->
                    if reader.IsDBNull i then "<NULL>" else reader.GetValue(i).ToString()
            |]
        printfn "%s" (String.Join(" | ", values))
        rowIndex <- rowIndex + 1
    if rowIndex = 0 then printfn "(no rows)"

let safeIdentifierRegex = Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled)

let isComplexType (dataType:string) =
    [ "STRUCT"; "LIST"; "MAP"; "UNION" ] |> List.exists dataType.Contains

let columnStats (conn:DuckDBConnection) path columns =
    columns
    |> List.choose (fun col ->
        if safeIdentifierRegex.IsMatch col.Name && not (isComplexType col.DataType) then
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"""
                SELECT
                    COUNT({sprintf "\"%s\"" col.Name}) AS non_null_rows,
                    COUNT(*) - COUNT({sprintf "\"%s\"" col.Name}) AS null_rows,
                    approx_count_distinct({sprintf "\"%s\"" col.Name}) AS approx_distinct
                FROM read_parquet('{escapeLiteral path}')
                """
            use reader = cmd.ExecuteReader()
            if reader.Read() then
                let getValue idx defaultValue =
                    if reader.IsDBNull idx then defaultValue else Convert.ToInt64(reader.GetValue idx)
                let nonNull = getValue 0 0L
                let nulls = getValue 1 0L
                let distinct = getValue 2 -1L
                Some { Column = col.Name; NonNullRows = nonNull; NullRows = nulls; ApproxDistinct = distinct }
            else None
        else None)

let sampleRows (conn:DuckDBConnection) path limit =
    use cmd = conn.CreateCommand()
    cmd.CommandText <- $"SELECT * FROM read_parquet('{escapeLiteral path}') LIMIT {limit}"
    use reader = cmd.ExecuteReader()
    renderTable reader

let topValues (conn:DuckDBConnection) path columns limit =
    columns
    |> List.iter (fun col ->
        if safeIdentifierRegex.IsMatch col.Name && not (isComplexType col.DataType) then
            use cmd = conn.CreateCommand()
            cmd.CommandText <-
                $"""
                SELECT {sprintf "\"%s\"" col.Name} AS value, COUNT(*) AS frequency
                FROM read_parquet('{escapeLiteral path}')
                GROUP BY 1
                ORDER BY frequency DESC
                LIMIT {limit}
                """
            use reader = cmd.ExecuteReader()
            printfn "\nColumn: %s" col.Name
            renderTable reader
        else
            printfn "\nColumn: %s (skipped - unsupported name or complex type)" col.Name)

let ensureDirectory (path:string) =
    let directory = Path.GetDirectoryName path
    if not (String.IsNullOrWhiteSpace directory) && not (Directory.Exists directory) then
        Directory.CreateDirectory directory |> ignore

let exportToJson (conn:DuckDBConnection) parquetPath jsonPath =
    ensureDirectory jsonPath
    if File.Exists jsonPath then File.Delete jsonPath
    use cmd = conn.CreateCommand()
    cmd.CommandText <-
        $"""
        COPY (SELECT * FROM read_parquet('{escapeLiteral parquetPath}'))
        TO '{escapeLiteral jsonPath}' (FORMAT JSON);
        """
    cmd.ExecuteNonQuery() |> ignore

let analyze path =
    failIfMissing path
    let info = FileInfo path
    printfn "Analyzing Parquet file: %s" path
    printfn "Last modified: %O" info.LastWriteTimeUtc
    printfn "Size: %s" (formatBytes info.Length)

    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()

    printDivider "Schema"
    let schema = readSchema conn path
    schema
    |> List.iteri (fun idx col ->
        printfn "%2d. %-30s %-20s Nullable=%s %s" (idx + 1) col.Name col.DataType col.IsNullable col.Extra)

    printDivider "Row count"
    let rows = readRowCount conn path
    printfn "Total rows: %d" rows

    printDivider "JSON export"
    exportToJson conn path jsonOutputPath
    printfn "Exported data to: %s" jsonOutputPath

    printDivider (sprintf "First %d rows" sampleRowCount)
    sampleRows conn path sampleRowCount

    printDivider (sprintf "Typed first %d rows" sampleRowCount)
    let typedSample = loadParquetRows conn path (Some sampleRowCount)
    typedSample
    |> List.iteri (fun idx row ->
        printfn "%2d. %s (%d messages)" (idx + 1) row.Key row.Messages.Length
        row.Messages
        |> List.iteri (fun mIdx msg ->
            let preview =
                if String.IsNullOrWhiteSpace msg.Content then "<empty>"
                else
                    let normalized = msg.Content.Replace("\n", " ").Trim()
                    if normalized.Length > 100 then normalized.Substring(0, 100) + "..." else normalized
            printfn "      [%d] %s: %s" (mIdx + 1) msg.Role preview)

        if not (List.isEmpty row.GroundTruth) then
            row.GroundTruth
            |> List.map summarizeGroundTruthInstruction
            |> fun items -> String.Join("; ", items)
            |> printfn "      Ground truth: %s")

    printDivider "Ground truth instructions"
    let instructionStats = groundTruthInstructionStats conn path
    printfn "Unique instruction IDs: %d" instructionStats.Length
    instructionStats
    |> List.truncate 30
    |> List.iter (fun (id, count) -> printfn "%-45s %10d" id count)

    printDivider "Column stats"
    let stats = columnStats conn path schema
    if List.isEmpty stats then
        printfn "No simple columns available for statistics."
    else
        stats
        |> List.iter (fun s ->
            let approxDistinctDisplay = if s.ApproxDistinct < 0L then "N/A" else s.ApproxDistinct.ToString()
            printfn "%-30s Non-null: %d | Null: %d | Approx distinct: %s" s.Column s.NonNullRows s.NullRows approxDistinctDisplay)

    printDivider (sprintf "Top %d values" sampleValueCount)
    topValues conn path schema sampleValueCount

#if RUN_ANALYZE
analyze parquetPath
#endif
//analyze parquetPath

let test() = 
    use conn = new DuckDBConnection("Data Source=:memory:")
    conn.Open()

    let firstTen : seq<IfbenchRow> = parquetRowSeq conn parquetPath (Some 10)
    firstTen
    |> Seq.toList
    //|> Seq.iter (fun row -> printfn "%s (%d messages)" row.Key row.Messages.Length)
 
;;


test()
