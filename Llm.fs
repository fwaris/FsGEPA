namespace FsGepa.Llm 
open FsGepa

module Llm = 
    let updatePrompt<'a,'b> cfg (prompt:Prompt) (evals:EvaledTask<'a,'b> list) =  async {
        let instr = 
            evals
            |> List.map (fun t ->
                $"""
question: {t.task.input}
feedback: {t.eval.feedback.text}
traces:{t.eval.traces |> List.map _.trace |> String.concat "\n" }
"""         )
            |> String.concat "\n"

        let metaPrompt = 
                [Vars.current_instruction, prompt.text :> obj; Vars.input_outputs_feedback, instr]
                |> Prompts.renderPrompt Prompts.metaPrompt 
            
        let! text = cfg.generate.generate None metaPrompt None
        
        return {text=text}
    }

