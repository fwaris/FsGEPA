//types and functions to support runtime processing
namespace FsGepa.Run
open FSharp.Control
open FsGepa


type GrSystem<'a,'b> = {
    id : string
    parent : string option
    sys : GeSystem<'a,'b> 
    evals : EvaledTask<'a,'b> list
}
    with member this.avgScore 
                with get() = 
                    if this.evals.IsEmpty then 0.0 
                    else this.evals |> List.averageBy (fun e -> e.eval.score)

type GrRun<'a,'b> = {
    count : int
    candidates : GrSystem<'a, 'b> list
    cfg : Config
    tasksPareto : seq<int * GeTask<'a,'b>>
    tasksFeedback : GeTask<'a,'b> seq
    comboSet : Set<Set<string>>
}

type ProposedCandidate<'a,'b> = {
    candidate : GeSystem<'a,'b>
    parentId : string
    parentMBScore : float
}

type MergeProposal<'a,'b>  = Merge of ProposedCandidate<'a,'b> option * Set<Set<string>>

type ProposeParms<'a,'b> = {
    pool : GrSystem<'a,'b> list
    cfg : Config
    tasksMB : (int*GeTask<'a,'b>) seq
    comboSet : Set<Set<string>>
}

module Run =

    let sampleMB cfg tasks = 
        let frac = float cfg.miniBatch / float cfg.totalFeedbackSize
        tasks |> Seq.filter(fun _ -> Utils.rng.NextDouble() <= frac) |> Seq.indexed |> Seq.toList

    let runTask cfg sys ((i,task):int*_) =  
        async {
            let! o = sys.flow cfg sys.modules task.input
            return (i,task),o
        }

    let evalTask cfg ((i,(t:GeTask<_,_>)),o) = 
        async {
            let! e = t.evaluate cfg o
            return {index=i; task=t;eval=e}
        }

    let score cfg sys tasks = 
        tasks 
        |> AsyncSeq.ofSeq
        |> AsyncSeq.mapAsync (runTask cfg sys)
        |> AsyncSeq.mapAsync (evalTask cfg)

    let averageScore cfg sys tasks = 
        score cfg sys tasks
        |> AsyncSeq.map (fun et -> et.eval.score)
        |> AsyncSeq.toBlockingSeq
        |> Seq.average
