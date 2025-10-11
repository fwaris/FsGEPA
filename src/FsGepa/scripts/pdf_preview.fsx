#load "packages.fsx"
open System
open System.IO
open Packages

type TaskJson = FSharp.Data.JsonProvider<"""{"key":"0","prompt":"Sample prompt","instruction_id_list":["count:keywords_multiple"],"kwargs":[{"keyword1":"alpha"}]}""">

let parseLine (line:string) =
    TaskJson.Parse line

let readItems (path:string) =
    path
    |> File.ReadLines
    |> Seq.filter (String.IsNullOrWhiteSpace >> not)
    |> Seq.map parseLine

let file = home @@ "Downloads" @@ "train-00000-of-00001.json"

let items = readItems file |> Seq.toList

items |> List.length
let i10 = items.[10]
i10.InstructionIdList
i10.Kwargs.[0].Keyword1

