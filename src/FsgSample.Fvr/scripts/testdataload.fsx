#load "packages.fsx"
#load "../../FsGepa/Utils.fs"
#load "../Data.fs"

let tail (s:string) = let i = s.IndexOf('_') in if i > 0 then Some (s.Substring(i)) else None

let knowns = ["sentence";"cell";"section"]
let tail2 (s:string) = tail s |> Option.filter (fun x -> knowns |> List.forall (fun k -> x.Contains k |> not))

let fdb = FsgIF.Data.loadFeverous FsgIF.Data.FEVEROUS
fdb |> Array.map (fun x->x.challenge) |> Array.distinct
fdb |> Array.map (fun x -> x.label) |> Array.distinct
let f1 = fdb |> Array.find (fun x -> x.id = 1350)
let titles = FsgIF.Data.extractPageTitles f1
let json = titles |> List.tryHead |> Option.bind FsgIF.Data.fetchPageJson |> fun x -> x.ToString() |> printfn "%s"
let contentTypes = fdb |> Seq.collect _.evidence |> Seq.collect (_.content) |> Seq.choose tail2 |> Seq.distinct |> Seq.toList
let ctxTypes = fdb |> Seq.collect _.evidence |> Seq.collect (fun e -> e.context |> Map.toSeq |> Seq.collect snd ) |> Seq.choose tail2 |> Seq.distinct |> Seq.toList

