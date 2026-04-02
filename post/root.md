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

- The input along with its associated meta information, including the ground-truth label (if any), is called a *Task* in GEPA. Required also is a function that can *evaluate* the output of the flow for any *Task* and provide a score [0..1] and optionally some *feedback*.

- Minimally, an instance of the GEPA algorithm accepts an initial *System*; a dataset of *tasks*; the *evaluation* function; and a *budget* of iterations to produce a *System* wih optimized prompts.

### GEPA Algorithm
GEPA stands for 'Genetic Pareto'. Its inspired by the venerable [Genetic Algorithm](https://optimization.cbe.cornell.edu/index.php?title=Genetic_algorithm) (GA). As in GA, GEPA maintains a population of candidate *Systems* that are evolved with (domain-specific) mutation and crossover operations. GEPA starts with a single root *System* and grows that into a population of candidate *Systems* over successive iterations. However unlike GA where the best candidate is always selected, GEPA draws 'parent(s)' for the next generation from a set of candidate *Systems* that are on the pareto frontier. An innovation that maintains healthy diversity, reducing the chances of being trapped into local optima.

The pareto frontier, and the GEPA versions of mutation and crossover are explained next. Note that the explanations are high level. See the paper for the authoritative definitions.

---

> #### Pareto Frontier
> From the datasets of *Tasks* given to GEPA, it sets aside a pool of 'pareto' *Tasks*. The candidate *Systems* are scored on this pareto list. The pareto frontier is the list of *Systems* that are *not dominated*. For example, if a single *System* scores the highest on each of the pareto *Tasks*, it would be the only one in the frontier. However, if any other candidate *System* does better on even one *Task* then that also would be in the pareto set.

> #### Mutation or *Reflective Prompt Update*
> In each iteration where the mutation strategy is selected, GEPA selects a single 'parent' *System* from the pareto set and updates a single *Module* prompt in it. Briefly, the process as follows:
> - Sample a mini-batch of *Tasks* from the input dataset (excluding pareto)
> - Run the parent *flow* on this set and *evaluate* the results. This will provide the outputs, traces, scores and feedbacks for each of the mini-batch *Tasks*.
> - Pack all of this information - including the existing selected *Module* prompt - into a *meta prompt* and invoke an LLM to generate a new prompt and consequently a new *Module* instance.
> - Create a new 'child' *System* that inherits everything from the parent except for the selected *Module*.

> #### Crossover or *System-Aware Merge*
> In an iteration where the crossover strategy is chosen, GEPA selects two parents from the pareto set and a common ancestor. GEPA create a new child that intermixes the *Modules* from the parents and the ancestor in a specific way (see the paper for details).

---

The child candidate *System* is added to the population *only* if its performance is better than its parents' over the aforementioned mini-batch, otherwise its discarded.

A graphical representation of the GEPA algorithm is given in Fig 1.

![GepaFlow](/post/imgs/gepa_alg.jpeg)
* Fig 1: GEPA Algorithm (source: GEPA paper)

### GEPA Results
GEPA study is comprehensive, comparing GEPA performance over several datasets against multiple competing approaches - notably [MIPROv2](https://arxiv.org/pdf/2510.04618) (the algorithm underpinning [DSPy](https://dispy.ai)) and [Reinforcement Learning with GRPO](https://www.datacamp.com/blog/what-is-grpo-group-relative-policy-optimization) (RL). Following are the notable takeaways:

- GEPA significantly outperforms RL in both accuracy and cost-efficiency, raising questions about whether fine-tuning offers real advantages over automated prompt tuning. The fact that even MIPROv2 surpasses RL reinforces this observation.

- When comparing automated prompt tuning methods, GEPA achieves superior results over MIPROv2 on all four evaluated datasets. By incorporating outputs, traces, and feedback into the meta prompt, GEPA's reflection-based approach enables the LLM to generate more effective instructions through deeper analysis of past performance.

- Beyond superior overall performance, reflection-tuned prompts also demonstrate stronger generalization compared to MIPROv2's few-shot, exemplar-based strategy. The authors credit this advantage to modern models' enhanced ability to follow complex instructions.

## FsGepa
FsGepa is an implementation of GEPA for the dotnet platform, written in the F# language. 

F# is a concise, functional-first programming language that defaults to immutability. Despite being strongly typed, F# features powerful type inference that provides the brevity of a scripting language while maintaining full type safety. 

The core GEPA algorithm implementation comprises approximately 1000 lines of F# code, developed over the course of a week.

A core benefit of functional languages is the availability of immutable, functional data structures (e.g. lists, maps [dictionaries], sets, etc.) that significantly reduce the code required to perform complex transformations. 

To use FsGepa, its best to start with the provided sample and adapt that for your own optimization setup. Start with the [readme.md of the Feverous sample](https://github.com/fwaris/FsGEPA/blob/main/src/FsgSample.Fvr/readme.md).

