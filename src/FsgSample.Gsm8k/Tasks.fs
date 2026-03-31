namespace FsgSample.Gsm8k
open System
open FsGepa

type Answer = {
    final_answer : string
    solution_pad : string option
}

module Tasks =
    let evalFn (_:Config) (input:Gsm8kInput) (flowResult:FlowResult<Answer>) : Async<Eval> = async {
        let predicted = Data.normalizeFinalAnswer flowResult.output.final_answer
        let expected = Data.normalizeFinalAnswer input.final_answer
        let score = if predicted = expected then 1.0 else 0.0
        let feedback =
            if score = 1.0 then
                "Assistant answered correctly."
            else
                $"Assistant should have answered `{input.final_answer}` but answered `{flowResult.output.final_answer}`."
        Log.info $"{input.id} score = {score}; GT:{input.final_answer}; Pred:{flowResult.output.final_answer}"
        return {
            score = score
            feedback = Feedback feedback
        }
    }

    let toTask (input:Gsm8kInput) =
        {
            input = input
            evaluate = evalFn
        }

    let taskSets paretoCount feedbackCount holdoutCount =
        let rng = Random(0)
        let shuffle (items:'a[]) =
            items
            |> Array.sortBy (fun _ -> rng.Next())

        let train = Data.loadTrainingInputs() |> shuffle
        let test = Data.loadTestInputs() |> shuffle

        if train.Length < paretoCount + feedbackCount then
            failwithf "Need at least %d training items in %s" (paretoCount + feedbackCount) Data.GSM8K_TRAIN

        if test.Length < holdoutCount then
            failwithf "Need at least %d test items in %s" holdoutCount Data.GSM8K_TEST

        let pareto =
            train
            |> Array.take paretoCount
            |> Array.map toTask
            |> Array.toList

        let feedback =
            train
            |> Array.skip paretoCount
            |> Array.take feedbackCount
            |> Array.map toTask
            |> Array.toList

        let holdout =
            test
            |> Array.take holdoutCount
            |> Array.map toTask
            |> Array.toList

        pareto, feedback, holdout
