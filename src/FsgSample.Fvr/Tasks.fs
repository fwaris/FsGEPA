namespace FsgSample.Fvr
open System
open System.IO
open System.Text.Json.Serialization
open FsGepa

type FeverousInput = {
    id : int
    claim : string
    label : string
    document : string
}

[<JsonConverter(typeof<JsonStringEnumConverter>)>]
[<RequireQualifiedAccess>]
type AnswerType =
    | SUPPORTS = 0
    | REFUTES = 1
    | NOT_ENOUGH_INFO = 2

type Answer = {answer:AnswerType}

module Tasks =
    let MHR = "Multi-hop Reasoning"
    let answers = [|"SUPPORTS"; "REFUTES"; "NOT ENOUGH INFO"|]

    let evalFn (cfg:Config) (input:FeverousInput) (flowResult:FlowResult<Answer>): Async<Eval> = async{
        
        let score = 
            match flowResult.output.answer with 
            | AnswerType.SUPPORTS when input.label === answers.[0] -> 1.0
            | AnswerType.REFUTES when input.label === answers.[1] -> 1.0
            | AnswerType.NOT_ENOUGH_INFO when input.label === answers.[2] -> 1.0
            | _ -> 0.0
        Log.info $"{input.id} score = {score}; GT:{input.label}; Pred: {flowResult.output.answer}"
        let feedback = if score = 1.0 then "Assistant answered correctly" else "Assistant should have answered: {input.label}"
        return
            {
                score = score
                feedback = Feedback feedback
            }
    }

    let toTask (fr:FeverousRecord) = 
        let doc = Data.generateDoc fr
        let inp = 
            {
               id = fr.id
               claim = fr.claim
               document = doc 
               label = fr.label
            }
        {
            input = inp
            evaluate = evalFn
        }

    let taskSets () = 
        let fdb = FsgSample.Fvr.Data.loadFeverous FsgSample.Fvr.Data.FEVEROUS
        let mhFdb = fdb |> Array.filter (fun r -> r.challenge = MHR) |> Array.take 100
        let sz = mhFdb.Length
        let szPareto = sz / 3 
        let szTest = sz / 5
        let taskPareto = mhFdb |> Array.take szPareto |> Seq.map toTask |> Seq.toList
        let tasksMb = mhFdb |> Array.skip szPareto |> Array.take szTest |> Seq.map toTask |> Seq.toList
        let tasksTest = mhFdb |> Array.skip (szPareto + szTest) |> Seq.map toTask |> Seq.toList
        taskPareto, tasksMb, tasksTest
        
