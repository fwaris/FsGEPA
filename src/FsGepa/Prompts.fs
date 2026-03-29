namespace FsGepa

module Vars = 
    let current_instruction = "current_instruction"
    let input_outputs_feedback =  "input_outputs_feedback"
    let selected_hypothesis = "selected_hypothesis"
    let module_constraints = "module_constraints"
    let hypothesis_count = "hypothesis_count"

module Prompts =
    open Microsoft.SemanticKernel    

    ///string encode json content for proper template handling
    let toJson<'t>(o:'t) =
        let str = Utils.formatJson o
        System.Text.Json.JsonSerializer.Serialize(str, Utils.openAIResponseSerOpts)

    ///create a KernelArguments instance which holds the
    ///values for prompt template variable names
    let kernelArgs (args:(string*obj) seq) =
        let settings = PromptExecutionSettings()
        let kwargs = KernelArguments(settings)
        for (k,v) in args do
            kwargs.Add(k,v)
        kwargs

    ///render a prompt template by replacing
    ///variable place holders in the template
    ///with the values held in the given KernelArguments
    let renderPromptWith (promptTemplate:string) (args:KernelArguments) =
        let b = Kernel.CreateBuilder()
        //b.Plugins.AddFromType<TimePlugin>("time") |> ignore
        let k = b.Build()
        let fac = KernelPromptTemplateFactory(AllowDangerouslySetContent=true)                        //<--- need to set it in both places
        let cfg = PromptTemplateConfig(template = promptTemplate, AllowDangerouslySetContent=true)      //<--- for it to work
        let pt = fac.Create(cfg)
        pt.RenderAsync(k, args).GetAwaiter().GetResult()

    ///render a prompt template by replacing
    ///variable place holders in the template
    ///with the values held in the given args
    let renderPrompt (promptTemplate:string) args =
        args
        |> kernelArgs
        |> renderPromptWith promptTemplate


    let metaPrompt = $"""
I provided an assistant with the following [instructions] to perform a task for me:
```
{{{{${Vars.current_instruction}}}}}
```

The following are examples of different task inputs provided to the assistant
along with the assistant's response for each of them, and some feedback on how
the assistant's response could be better:
```
{{{{${Vars.input_outputs_feedback}}}}}
```

Read the inputs carefully and identify the input format and infer detailed task
description about the task I wish to solve with the assistant.
Read all the assistant responses and the corresponding feedback. Identify all
niche and domain specific factual information about the task and include it in
the instruction, as a lot of it may not be available to the assistant in the
future. The assistant may have utilized a generalizable strategy to solve the
task, if so, include that in the instruction as well.
"""

    let vistaHypothesisPrompt = $"""
I provided an assistant with the following [instructions] to perform a task for me:
```
{{{{${Vars.current_instruction}}}}}
```

The following are examples of different task inputs provided to the assistant
along with the assistant's response for each of them, and some feedback on how
the assistant's response could be better:
```
{{{{${Vars.input_outputs_feedback}}}}}
```

Module-specific update guidance, if any:
```
{{{{${Vars.module_constraints}}}}}
```

You are diagnosing why the current instructions fail. Do not rewrite the
instructions yet.

Return JSON with a `hypotheses` array containing at most
{{{{${Vars.hypothesis_count}}}}} entries. Each entry must have:
- `id`: short kebab-case identifier
- `label`: concise root-cause title
- `summary`: what is wrong and why it matters
- `evidence`: concrete patterns supported by the examples
- `priority`: integer rank, where higher means more urgent
- `confidence`: number from 0.0 to 1.0

Prefer structural prompt defects over one-off content patches. Focus on issues
such as missing constraints, wrong ordering, underspecified output rules,
reasoning/tool-use gaps, or latent defects a stronger model might be masking.
"""

    let vistaRewritePrompt = $"""
I provided an assistant with the following [instructions] to perform a task for me:
```
{{{{${Vars.current_instruction}}}}}
```

The following are examples of different task inputs provided to the assistant
along with the assistant's response for each of them, and some feedback on how
the assistant's response could be better:
```
{{{{${Vars.input_outputs_feedback}}}}}
```

Module-specific update guidance, if any:
```
{{{{${Vars.module_constraints}}}}}
```

Apply exactly one diagnosed hypothesis when rewriting the instructions:
```json
{{{{${Vars.selected_hypothesis}}}}}
```

Rewrite the instructions so they directly address that hypothesis while
preserving any behavior that already appears correct. Preserve field names,
schemas, and externally visible output requirements unless the hypothesis
explicitly identifies them as defective.

Return JSON with:
- `instructions`: the revised prompt text
- `change_summary`: short explanation of what changed
"""
