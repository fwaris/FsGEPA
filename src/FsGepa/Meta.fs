namespace FsGepa.Llm 
open FsGepa

module Meta = 
    let truncate len (s:string) = if s.Length < len then s else s.Substring(0,len) + "...[elided]"

    let rec internal callGenerate attempts (generate:IGenerate) model systemMessage messages responseFormat opts = async {
        try 
            let! resp = generate.generate model systemMessage messages responseFormat None
            return resp
        with ex -> 
            if attempts > 0 then 
                Log.warn $"generate call failed. retry {attempts - 1}, error: {ex.Message}"
                do! Async.Sleep 3000
                return! callGenerate (attempts - 1) generate model systemMessage messages responseFormat opts
            else 
                Log.exn (ex,"callGenerate")
                return raise ex
    }

    let generatePrompt<'a,'b> cfg modulePrompt metaPromptTemplate (evals:EvaledTask<'a,'b> list) additionalInstr = async {

        let feedback = 
            evals
            |> List.map (fun t ->
                let trace = t.flowResult.traces |> List.head
                let thinking = 
                    trace.reasoning 
                    |> Option.map(fun x -> $"## ASSISTANT THOUGHTS: {x}") 
                    |> Option.defaultValue ""
                let feedback = 
                    t.eval.feedback.text() 
                    |> checkEmpty
                    |> Option.map (fun x -> "##EVAL FEEDBACK: {X}") 
                    |> Option.defaultValue ""
                $"""
# -- EXAMPLE START --
## TASK INPUT: {trace.inputPrompt |> truncate cfg.max_sample_input_prompt_length}
## ASSISTANT RESPONSE : {trace.response}
{feedback}
{thinking}
--- EXAMPLE END ---
"""         )
            |> String.concat "\n"

        let metaPrompt = 
                [Vars.current_instruction, modulePrompt :> obj; Vars.input_outputs_feedback, feedback]
                |> Prompts.renderPrompt metaPromptTemplate

        let metaPrompt = 
            match additionalInstr with 
            | Some addInstr -> $"{metaPrompt}\n\n{addInstr}"
            | None -> metaPrompt
            
        let! text = callGenerate 5 cfg.generate cfg.default_model None [{role="user"; content=metaPrompt}] None None
        let instr = extractQuoted text.output |> Template.normalizePrompt
        
        return instr
    }

    let updatePromptOverride<'a,'b> cfg modulePrompt (mMeta:ModuleMetaPrompt) (evals:EvaledTask<'a,'b> list) =  async {
        let! newPrompt = generatePrompt cfg modulePrompt mMeta.metaPrompt evals None
        match! mMeta.validate newPrompt with 
        | Some issues -> 
            let! revisedPrompt = generatePrompt cfg modulePrompt mMeta.metaPrompt evals (Some $"Taking into account the following additional directions:\n {issues}")
            return {text = revisedPrompt}
        | None -> return {text = newPrompt}
    }

    let updatePrompt<'a,'b> cfg (gModule:GeModule) (evals:EvaledTask<'a,'b> list) =  async {
        match gModule.metaPrompt with 
        | Some mMeta -> return! updatePromptOverride cfg gModule.prompt.text mMeta evals
        | None -> let! text = generatePrompt cfg gModule.prompt.text Prompts.metaPrompt evals None
                  return {text=text}
    }

