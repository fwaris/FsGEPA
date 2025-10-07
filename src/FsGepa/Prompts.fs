namespace FsGepa

module Vars = 
    let current_instruction = "current_instruction"
    let input_outputs_feedback =  "input_outputs_feedback"

module Prompts =
    open Microsoft.SemanticKernel    

    ///string encode json content for proper template handling
    let toJson<'t>(o:'t) =
        let str = Utils.formatJson o
        System.Text.Json.JsonSerializer.Serialize(str, Utils.openAIResponseSerOpts)

    ///create a KernelArguments instance which holds the
    ///values for prompt template variable names
    let kernelArgs (args:(string*obj) seq) =
        let sttngs = PromptExecutionSettings()
        let kargs = KernelArguments(sttngs)
        for (k,v) in args do
            kargs.Add(k,v)
        kargs

    ///render a prompt template by replacing
    ///variable place holders in the template
    ///with the values held in the given KernelArguments
    let renderPromptWith (promptTemplate:string) (args:KernelArguments) =
        (task {
            let b = Kernel.CreateBuilder()
            //b.Plugins.AddFromType<TimePlugin>("time") |> ignore
            let k = b.Build()
            let fac = KernelPromptTemplateFactory( AllowDangerouslySetContent=true)                        //<--- need to set it in both places
            let cfg = PromptTemplateConfig(template = promptTemplate,AllowDangerouslySetContent=true)      //<--- for it to work
            let pt = fac.Create(cfg)
            let! rslt = pt.RenderAsync(k,args) |> Async.AwaitTask
            return rslt
        }).Result //async not needed as all local

    ///render a prompt template by replacing
    ///variable place holders in the template
    ///with the values held in the given args
    let renderPrompt (promptTemplate:string) args =
        args
        |> kernelArgs
        |> renderPromptWith promptTemplate


    let metaPrompt = $"""
I provided an assistant with the following instructions to perform a task for me:
```
{{{{${Vars.current_instruction}}}}}
```
The following are examples of different task inputs provided to the assistant
along with the assistant's response for each of them, and some feedback on how
the assistant's response could be better:
```
{{{{${Vars.input_outputs_feedback}}}}}
```
Your task is to write a new instruction for the assistant.

Read the inputs carefully and identify the input format and infer detailed task
description about the task I wish to solve with the assistant.
Read all the assistant responses and the corresponding feedback. Identify all
niche and domain specific factual information about the task and include it in
the instruction, as a lot of it may not be available to the assistant in the
future. The assistant may have utilized a generalizable strategy to solve the
task, if so, include that in the instruction as well.
Provide the new instructions within ``` blocks.
"""
