# Automated Prompt Tuning with FsGepa

A comprehensive recent study - [GEPA: Reflective Prompt Evolution Can Outperform Reinforcement Learning](https://arxiv.org/abs/2507.19457) - has shown that automated prompt tuning is a cheaper, faster, more effective alternative to Reinforcement Learning, for a broad swath of use cases. This is a seminal result that can't really be ignored.

Practically speaking, automated prompt tuning can provide significant cost savings. In my testing, I was able to gain 90% accuracy with [`gpt-oss-20b`](https://docs.api.nvidia.com/nim/reference/openai-gpt-oss-20b) plus automated prompt tuning on a classification task. Very comparable results to [`gpt-5-mini`](https://platform.openai.com/docs/models/gpt-5-mini) on the same task (with milder prompt tuning). Considering that `gpt-5-mini` costs [**$2.0**](https://platform.openai.com/docs/models/gpt-5-mini) per million output tokens and `gpt-oss-20b` [**$0.3**](https://aws.amazon.com/bedrock/pricing/) for the same - the value of automated prompt tuning becomes self evident. Consumption rate of 1B output tokens / month is not uncommon for many companies. This means about $2K/mth for `gpt-5-mini` and only $300/mth for `gpt-oss-20b` (just for output).

Furthermore, manual prompt tuning is tedious and labor-intensive. Also prompts tuned for a particular model may have to be re-tuned if the workload is moved to another. *Why not apply AI to AI to multiply the productivity gains!*

[FsGepa](https://github.com/fwaris/FsGepa) is an open-source implementation of the GEPA algorithm for the [dotnet](https://dotnet.microsoft.com/en-us/) platform. However before delving into the specifics of GEPA and how to use FsGepa, lets view a sample result.

## FsGepa Example
The task is to consider a *claim* and *supporting facts* and determine if the facts `SUPPORT` / `REFUTE` the claim or there is `NOT ENOUGH INFO`.

The formulation and data for the task is available from [fever.ai](https://fever.ai/dataset/feverous.html). 

> #### Sample Record
> - **Claim**: *Algebraic logic has five Logical system and Lindenbaum–Tarski algebra which includes Physics algebra and Nodal algebra (provide models of propositional modal logics).*
>
> - **Supporting Facts**:
>   - *In mathematical logic , algebraic logic is the reasoning obtained by manipulating equations with free variables.*
>   - ... [link to full record](https://fever.ai/dataset_viewer/feverous/0.html)

The FsGepa setup for the task is as follows:
- Step 1: Given the claim and supporting facts summarize the supporting facts in relation to the claim.

- Step 2: From the claim and the summary from #1, determine the answer [support/refute/not enough info].

**Here is the initial prompt for Step 1**:
```
Given the fields `claim` and `supporting_facts`, produce the field `summary`.
```

**And the final FsGepa optimized prompt**:

---
```markdown
You are given two fields, **claim** and **supporting_facts**. Your task is to produce a third field, **summary**, that concisely states whether the claim is supported, partially supported, or refuted by the evidence in *supporting_facts*. The output must be a JSON object containing only the key `summary` whose value is a short English sentence or paragraph (no additional keys or text).

**Key requirements**
1. **Extract evidence** - The `supporting_facts` text may contain Markdown headings, tables, numbered sentences, and plain prose.  Parse these structures to locate information that directly addresses the elements of the claim (e.g., dates, names, numbers, relationships).  Do not assume the fact that is not explicitly present.
2. **Determine veracity** - Compare each element of the claim to the extracted evidence:
   * If all elements are present and the evidence is consistent, the claim is **supported**.
   * If some elements are present and consistent but others are missing or contradicted, the claim is **partially supported**.
   * If any element is contradicted by the evidence, the claim is **refuted**.
3. **Write the summary** - The summary should:
   * Begin with "The claim is supported", "The claim is partially supported", or "The claim is refuted".
   * Briefly mention the key piece(s) of evidence that led to that conclusion (e.g., the fact that X was elected in Y, or that a table shows Z).
   * Avoid extraneous detail or speculation.
4. **Formatting** - Return exactly:
```json
{ "summary": "..." }```

   No other keys, no plain-text commentary, no Markdown.

**Domain-specific notes**
- Claims may involve historical events, sports statistics, scientific facts, or biographical details.
- Tables often encode the crucial data; pay attention to headers such as "Pos", "Team", "Votes", "Population", etc.
- Sentences may be marked with numbers (e.g., **Sentence 3:**).  Use these to locate precise statements.
- If the claim contains multiple clauses (e.g., "X joined Y and Z happened"), treat each clause separately; partial support applies if only some clauses are verified.
- When evidence is absent for a clause, that clause is considered unsupported.  Do not infer or hallucinate facts.

**Example**
- Claim: "Rohan Pradeep Kumara's personal record in 2000 was 45.25, and he won gold in the 200 m at the 2004 South Asian Games."
- Supporting facts include a sentence stating the 2000 record is 45.25 and a table showing a gold medal in the 200 m.  All clauses are verified ␦ "The claim is supported ."
```
---

### Observations
- As per the GEPA paper, the initial prompt is purposefully kept minimal to give the LLM the freedom to evolve it unencumbered by any significant biases.

- As the result shows, the evolved prompt is comprehensive with detailed instructions and example(s).

- The result was achieved with a *budget* of only 20 iterations. The examples given in the GEPA paper have much higher *budgets* indicating that the sample performance may be improved further.

## GEPA Overview
A birds-eye perspective of GEPA is presented below. See the paper for the precise details.

- The goal of GEPA is to jointly optimize a set of prompts that are contained within an end-to-end flow. In GEPA parlance, the *flow* is referred as a *System* - wherein are *Modules* that contain the prompts. A flow is a process; it may invoke external tools; search documents; and/or invoke modules, possibly multiple times. There is really no stipulation from GEPA on how the flow is implemented (excepts perhaps that the prompts are used in some meaningful way).

- The flow consumes some input and produces an output and optionally ancillary information such as reasoning traces.

- The input along with its associated meta information, including the ground-truth label (if any), is called a *task* in GEPA. Required also is a function that can *evaluate* the output of the flow for any task and provide *feedback*.

- Minimally, an instance of the GEPA algorithm accepts an initial *System*; a dataset of *tasks*; and the evaluation function to produce a *System* wih optimized prompts.

### GEPA Algorithm






A F# implementation of GEPA (Genetic Evolutionary Prompt Augmentation) for optimizing compound AI systems.

- [GEPA: Reflective Prompt Evolution Can Outperform Reinforcement Learning](https://arxiv.org/abs/2507.19457).

(Start with the included [sample](/src/FsgSample.Fvr/readme.md) to understand how to use FsGepa)

## Overview
Automated prompt tuning can become expensive due to the iterative nature of the optimization process requiring the consumption of a large number of tokens. As compared to other automated prompt tuning algorithms (e.g. [MIPROv2 found in DSPY](https://arxiv.org/pdf/2510.04618)), GEPA is shown to be more cost effective - i.e., achieves better results for the same number of trials (or roll outs).

GEPA is meant to jointly optimize a set of prompts contained in a **multi-step flow** (referred to as Compound AI System or just *System* heretofore).

Inspired by Genetic Algorithm (GA), GEPA uses forms of mutation and cross-over to 'evolve' new *Systems* from a population of candidate *Systems*. See the paper for details but briefly:
- In mutation or *Reflective Update*, existing prompts are evolved by 'reflecting' on the existing prompt along with the associated inputs, outputs and feedbacks from a batch of sample results - *including 'think' or reasoning output*, if any.
- In cross-over or *System-Aware Merge*, a new candidate *System* is proposed by taking a pair of existing candidates and their common parent *Systems* and merging their prompts together in a specific way.

### Key Points:
- *Pareto Frontier*: To avoid local optima traps, GEPA considers a set of candidate *Systems* that are on the pareto frontier (which respect to a fixed set of 'pareto' input tasks) when selecting candidates for update operations. This quality-diversity strategy provides better exploration-exploitation balance over always selecting the best candidate, as is done in traditional GA.

- *Reflection*: The GEPA paper shows that reflection-tuned prompts generalize better (in addition to performing better overall) than the few-shot, exemplar based approach used by MIPROv2. The authors attribute this shift to improved instruction-following capability of newer models.

- *Frugality*: GEPA uses a small ('mini batch') sample of tasks (that are randomly selected from a larger set) to evaluate new candidate proposals. Only if the proposed candidate performs better than the donor parent, is it considered for evaluation on a the larger set. 

## Performance
The performance of GEPA is already established. The paper is comprehensive with several ablation studies demonstrating GEPA superiority (as-of 10/2025) over other considered approaches.

For the sample [included in this repo](/src/FsgSample.Fvr/readme.md), FsGepa achieves ~90% accuracy on the hold out set, in contrast to the baseline accuracy of ~60% over the same (model=gpt-oss-20b). This is achieved with a budget of only 20 iterations. The sample selected is relatively simple. Its main purpose is to explain how to use FsGepa. Extensive setup (e.g. document indexes) is not required. Only two data files need be downloaded. 

## Implementation Notes
The FsGepa implementation aims to be faithful to the algorithms outlined in the paper but there are always differences between theory and implementation. Since there is no author-supplied implementation as of yet (10/'25), consider this as best-effort.

## Other Notes
Optimization requires access to GPU-based LLM models and may be costly due to the iterative nature of the optimization process (i.e. repeated invocations of the LLM model APIs). Substantial development was done with locally running GPU models 
