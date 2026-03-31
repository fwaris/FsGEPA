namespace FsGepa

module Vars = 
    let current_instruction = "current_instruction"
    let input_outputs_feedback =  "input_outputs_feedback"
    let selected_hypothesis = "selected_hypothesis"
    let selected_label = "selected_label"
    let selected_fix = "selected_fix"
    let module_constraints = "module_constraints"
    let hypothesis_count = "hypothesis_count"
    let optimization_trace = "optimization_trace"
    let error_taxonomy = "error_taxonomy"
    let excluded_hypotheses = "excluded_hypotheses"
    let restart_observation = "restart_observation"
    let restart_issue = "restart_issue"

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

    let vistaHeuristicTaxonomy = """
- id: cot_field_ordering
  name: CoT / Output Field Ordering Defect
  description: The output schema requires the final answer before the reasoning steps, preventing chain-of-thought from influencing the result.
- id: format_and_syntax
  name: Format / Syntax Defect
  description: The prompt does not strictly enforce output schema, key set, or syntax validity.
- id: task_instruction_clarity
  name: Task Instruction / Constraint Defect
  description: Task goals or constraints are ambiguous, contradictory, or incomplete.
- id: reasoning_strategy
  name: Reasoning Strategy / Logic Defect
  description: The prompt implies a flawed or suboptimal reasoning procedure for the task.
- id: missing_domain_knowledge
  name: Missing Domain Knowledge Gap
  description: The prompt lacks necessary domain facts or definitions required for solving.
- id: edge_case_handling
  name: Edge Case / Boundary Defect
  description: The prompt handles common inputs but fails on boundary or atypical cases.
- id: unclassified_custom
  name: Unclassified / Custom Discovery
  description: None of the predefined categories fit; discover and justify a latent failure mode.
"""

    let vistaHeuristicHypothesisPrompt = $"""
I provided an assistant with the following [instructions] to perform a task for me:
```
{{{{${Vars.current_instruction}}}}}
```

The following are failed task cases from the latest minibatch:
```
{{{{${Vars.input_outputs_feedback}}}}}
```

Module-specific update guidance, if any:
```
{{{{${Vars.module_constraints}}}}}
```

Optimization trace so far:
```
{{{{${Vars.optimization_trace}}}}}
```

Heuristic error taxonomy:
```
{{{{${Vars.error_taxonomy}}}}}
```

Already proposed this round:
```
{{{{${Vars.excluded_hypotheses}}}}}
```

You are the VISTA hypothesis agent. Diagnose exactly one root cause using the
heuristic taxonomy above. Do not rewrite the instructions yet.

Return JSON with:
- `id`: the exact taxonomy `id`
- `label`: the taxonomy `name`
- `summary`: one or two sentences describing the concrete root cause
- `suggestedFix`: one or two sentences describing how to fix the prompt
- `evidence`: concrete patterns supported by the failed cases
- `priority`: integer rank, where higher means more urgent
- `confidence`: number from 0.0 to 1.0

Prefer structural prompt defects over one-off patches. Use the optimization
trace to avoid repeating stale directions unless the new failed cases show
materially different evidence.
"""

    let vistaFreeHypothesisPrompt = $"""
I provided an assistant with the following [instructions] to perform a task for me:
```
{{{{${Vars.current_instruction}}}}}
```

The following are failed task cases from the latest minibatch:
```
{{{{${Vars.input_outputs_feedback}}}}}
```

Module-specific update guidance, if any:
```
{{{{${Vars.module_constraints}}}}}
```

Optimization trace so far:
```
{{{{${Vars.optimization_trace}}}}}
```

Already proposed this round:
```
{{{{${Vars.excluded_hypotheses}}}}}
```

You are the VISTA free-hypothesis branch. Discover exactly one plausible root
cause that may fall outside the predefined taxonomy. Do not rewrite the
instructions yet.

Return JSON with:
- `id`: short kebab-case identifier for the discovered failure mode
- `label`: concise root-cause title
- `summary`: one or two sentences describing the concrete root cause
- `suggestedFix`: one or two sentences describing how to fix the prompt
- `evidence`: concrete patterns supported by the failed cases
- `priority`: integer rank, where higher means more urgent
- `confidence`: number from 0.0 to 1.0

Use the trace to avoid already-explored directions and prefer hypotheses that
explain why prior fixes stalled, oscillated, or failed to transfer.
"""

    let vistaRewritePrompt = $"""
I provided an assistant with the following [instructions] to perform a task for me:
```
{{{{${Vars.current_instruction}}}}}
```

The following are failed task cases from the latest minibatch:
```
{{{{${Vars.input_outputs_feedback}}}}}
```

Module-specific update guidance, if any:
```
{{{{${Vars.module_constraints}}}}}
```

Apply exactly one diagnosed hypothesis when rewriting the instructions:
Root cause label: {{{{${Vars.selected_label}}}}}

Hypothesis:
```json
{{{{${Vars.selected_hypothesis}}}}}
```

Suggested fix:
{{{{${Vars.selected_fix}}}}}

Rewrite the instructions so they directly address that hypothesis while
preserving any behavior that already appears correct. Make targeted edits only.
Preserve field names, schemas, and externally visible output requirements
unless the hypothesis explicitly identifies them as defective.

Return JSON with:
- `instructions`: the revised prompt text
- `change_summary`: short explanation of what changed
"""

    let vistaRestartPrompt = $"""
You are initializing a prompt from the model's natural behavior rather than
from an inherited seed.

Current draft prompt (may be blank):
```
{{{{${Vars.current_instruction}}}}}
```

Module-specific update guidance, if any:
```
{{{{${Vars.module_constraints}}}}}
```

Observed model output under the current draft:
```
{{{{${Vars.restart_observation}}}}}
```

Observed issue from evaluation/parsing:
```
{{{{${Vars.restart_issue}}}}}
```

Write or revise the prompt so the model's natural behavior is shaped into a
well-formed task instruction without inheriting defects from the previous seed.
Preserve externally required schemas and interfaces when they are explicitly
described in the module guidance above.

Return JSON with:
- `instructions`: the revised prompt text
- `change_summary`: short explanation of the initialization or revision
"""
