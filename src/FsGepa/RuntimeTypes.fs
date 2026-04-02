//type to support runtime processing
namespace FsGepa.Run
open FsGepa


type GrSystem<'a,'b> = {
    id : string
    parent : string option
    sys : GeSystem<'a,'b> 
    evals : EvaledTask<'a,'b> list
    origin : CandidateOrigin
    history : OptimizationTraceEntry list
}
    with member this.avgScore = lazy(
                if this.evals.IsEmpty then 0.0 
                    else this.evals |> List.averageBy (fun e -> e.eval.score))

type GrRun<'a,'b> = {
    count : int
    metricCalls : int
    cfg : Config
    candidates : GrSystem<'a, 'b> list
    tasksPareto : seq<int * GeTask<'a,'b>>
    tasksFeedback : GeTask<'a,'b> seq
    comboSet : Set<Set<string>>
    currentBest : Map<string,string> option
    seedId : string
    stalled : int
}

type ProposedCandidate<'a,'b> = {
    candidate : GeSystem<'a,'b>
    parentId : string
    parentMBScore : float
    candidateMBScore : float option
    origin : CandidateOrigin
    traceEntry : OptimizationTraceEntry
    metricCost : int
}

type MergeProposal<'a,'b>  = Merge of ProposedCandidate<'a,'b> option * Set<Set<string>>

type ProposePrams<'a,'b> = {
    step : int
    pool : GrSystem<'a,'b> list
    cfg : Config
    tasksMB : (int*GeTask<'a,'b>) seq
    comboSet : Set<Set<string>>
}
