namespace FsGepa.Run
open FsGepa

module Tlm = 
    open System.Threading.Channels

    let post (channel:Channel<Telemetry>) msg = 
        channel.Writer.TryWrite(msg) |> ignore

    let postFrontier cfg (pool:GrSystem<_,_> list) =
        cfg.telemetry_channel
        |> Option.iter (fun c -> 
            pool
            |> List.map(fun s -> s.id,s.avgScore.Value)
            |> Frontier
            |> post c
        )
    
    let postNewBest cfg currentBest (xs:GrSystem<_,_> list) = 
        cfg.telemetry_channel
        |> Option.bind (fun c -> 
            let cBest = xs |> List.maxBy(fun grSys -> grSys.avgScore.Value)
            let cBestMs = cBest.sys.modules |> Map.map(fun k v -> v.prompt.text)
            let newBest =
                match currentBest with 
                | Some ms when ms <> cBestMs -> Some cBestMs
                | None -> Some cBestMs
                | _    -> None
            match newBest with 
            | Some _ -> NewBest (cBestMs, cBest.avgScore.Value) |> post c
            | None -> ()
            newBest
        )
    
    let postAddReflective cfg score parentScore = 
        cfg.telemetry_channel
        |> Option.iter(fun c -> AddReflective {|score=score; parentScore=parentScore|} |> post c)

    let postAddMerge cfg score parentScore = 
        cfg.telemetry_channel
        |> Option.iter(fun c -> AddMerge {|score=score; parentScore=parentScore|} |> post c)

    let postAdd cfg byMerge score parentScore = 
        if byMerge then 
            postAddMerge cfg score parentScore
        else 
            postAddReflective cfg score parentScore

    let postGeneratedPrompt cfg prompt = 
        cfg.telemetry_channel
        |> Option.iter(fun c -> GeneratedPrompt prompt |> post c)


