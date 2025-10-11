#r "nuget:Parquet.Net,4.18.0"
open System
open System.IO
open Parquet

let path = "/Users/Faisal.Waris1/Downloads/train-00000-of-00001.parquet"
let stream = File.OpenRead path
let reader =
    ParquetReader.CreateAsync(stream)
    |> Async.AwaitTask
    |> Async.RunSynchronously
printfn "Row groups: %d" reader.RowGroupCount
printfn "Created by: %s" reader.Metadata.CreatedBy
let schema = reader.Schema
schema.GetDataFields()
|> Seq.iter (fun field ->
    let members =
        field.GetType().GetMembers()
        |> Array.map (fun m -> m.Name)
        |> Array.distinct
        |> Array.filter (fun name -> name.Contains("Type") || name.Contains("Null"))
    printfn "Field %s members: %A" field.Name members
    printfn "  Path: %A" field.Path
    printfn "  SchemaType: %A" field.SchemaType)
printfn "Metadata row groups: %d" reader.Metadata.RowGroups.Count
reader.Metadata.RowGroups
|> Seq.iteri (fun idx rg -> printfn "Metadata row group %d rows: %d" idx rg.NumRows)
let rowGroupMembers = reader.Metadata.RowGroups.[0].GetType().GetMembers() |> Array.map (fun m -> m.Name) |> Array.distinct
printfn "Row group metadata members: %A" rowGroupMembers
let firstMeta = reader.Metadata.RowGroups.[0]
let columnMembers =
    firstMeta.Columns
    |> Seq.head
    |> fun col -> col.GetType().GetMembers() |> Array.map (fun m -> m.Name) |> Array.distinct
printfn "Column metadata members: %A" columnMembers
let columnMetaMembers =
    firstMeta.Columns
    |> Seq.head
    |> fun col -> col.MetaData.GetType().GetMembers() |> Array.map (fun m -> m.Name) |> Array.distinct
printfn "Column MetaData members: %A" columnMetaMembers
let firstColumnStats = firstMeta.Columns |> Seq.head |> fun col -> col.MetaData.Statistics
if isNull firstColumnStats then
    printfn "No stats"
else
    printfn "Stats null=%A distinct=%A min=%A max=%A" firstColumnStats.NullCount firstColumnStats.DistinctCount firstColumnStats.Min firstColumnStats.Max
    if firstColumnStats.NullCount.HasValue then
        printfn "Null count type: %A" (firstColumnStats.NullCount.Value.GetType())
firstMeta.Columns
|> Seq.iteri (fun idx col ->
    let path = String.Join("/", col.MetaData.PathInSchema)
    printfn "Meta column %d path: %s" idx path)
for i in 0 .. reader.RowGroupCount - 1 do
    use rg = reader.OpenRowGroupReader(i)
    let members =
        rg.GetType().GetMembers()
        |> Array.map (fun m -> m.Name)
        |> Array.distinct
        |> Array.filter (fun name -> name.Contains("Meta") || name.Contains("Row"))
    printfn "Row group %d members: %A" i members
    let firstField = schema.GetDataFields() |> Seq.head
    let column =
        rg.ReadColumnAsync(firstField)
        |> Async.AwaitTask
        |> Async.RunSynchronously
    printfn "First column %s has %d values" column.Field.Name column.Data.Length
reader.Dispose()
stream.Dispose()
