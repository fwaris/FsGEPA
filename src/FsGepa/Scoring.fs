//types and functions to support runtime processing
namespace FsGepa.Run
open FSharp.Control
open FsGepa
open AsyncExts

module Scoring =

    let sampleMB cfg tasks = 
        let sampleSize = max 1 (min cfg.mini_batch_size cfg.feedback_tasks_count)
        tasks
        |> Seq.indexed
        |> Seq.sortBy (fun _ -> Utils.rng.Next())
        |> Seq.truncate sampleSize
        |> Seq.toList

    let rec runTask attempts cfg sys ((i,task):int*GeTask<_,_>) =  
        async {
            try
                Log.info $"run task {i}"
                let! o = sys.flow cfg sys.modules task.input
                return i,task,o
            with ex -> 
                if attempts > 0 then 
                    Log.warn $"runTask failed, attempts remain: {attempts - 1}. Error: {ex.Message}"
                    do! Async.Sleep 3000
                    return! (runTask (attempts - 1) cfg sys (i,task))
                else
                    Log.exn (ex,"runTask")
                    return raise ex
        }

    let evalTask cfg (i,(t:GeTask<_,_>),flowResult) = 
        async {
            let! e = t.evaluate cfg t.input flowResult
            return {index=i; task=t;eval=e; flowResult=flowResult}
        }

    let score cfg sys tasks = 
        tasks 
        |> AsyncSeq.ofSeq
        |> AsyncSeq.mapAsyncParallelThrottled cfg.flow_parallelism (runTask 5 cfg sys)
        |> AsyncSeq.mapAsync (evalTask cfg)

    let averageScore cfg sys tasks = 
        score cfg sys tasks
        |> AsyncSeq.map (fun et -> et.eval.score)
        |> AsyncSeq.toBlockingSeq
        |> Seq.average

    let withModelOverride model (sys:GeSystem<'a,'b>) =
        {
            sys with
                modules =
                    sys.modules
                    |> Map.map (fun _ m -> {m with model = Some model})
        }

    let averageScoreMultiModel cfg models sys tasks =
        match models with
        | [] -> averageScore cfg sys tasks
        | xs ->
            xs
            |> List.map (fun model -> withModelOverride model sys)
            |> List.averageBy (fun candidate -> averageScore cfg candidate tasks)
