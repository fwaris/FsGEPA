//types and functions to support runtime processing
namespace FsGepa.Run
open FSharp.Control
open FsGepa
open AsyncExts

module Scoring =

    let sampleMB cfg tasks = 
        let frac = float cfg.mini_batch_size / float cfg.feedback_tasks_count
        tasks |> Seq.filter(fun _ -> Utils.rng.NextDouble() <= frac) |> Seq.indexed |> Seq.toList

    let rec runTask attempts cfg sys ((i,task):int*GeTask<_,_>) =  
        async {
            try
                Log.info $"run task {i}"
                let! o = sys.flow cfg sys.modules task.input
                return i,task,o
            with ex -> 
                if attempts < 0 then 
                    Log.warn $"run task attempt failed {attempts - 1}"
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
        |> AsyncSeq.mapAsyncParallelThrottled cfg.flow_parallelism (runTask 4 cfg sys)
        |> AsyncSeq.mapAsync (evalTask cfg)

    let averageScore cfg sys tasks = 
        score cfg sys tasks
        |> AsyncSeq.map (fun et -> et.eval.score)
        |> AsyncSeq.toBlockingSeq
        |> Seq.average
