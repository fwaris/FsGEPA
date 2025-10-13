# FsGepa

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

- *Frugality*: GEPA uses a small ('mini batch') sample of tasks (that are randomly selected from a larger set) to evaluate new candidate proposals. Only if the proposed candidate performs better than the donor parent, is it considered for evaluation on a the larger set. 

## Performance
The performance of GEPA has already been well established. The original paper provides a comprehensive evaluation, including multiple ablation studies demonstrating GEPA’s superiority (as of October 2025) over other comparable approaches.

To verify and validate this specific implementation of GEPA, a sample project — [FsgSample.Fvr](/src/FsgSample.Fvr/) — was developed and is included in the repository. See its accompanying  [readme.md](/src/FsgSample.Fvr/readme.md) for details.

In the provided setup, FsGepa consistently achieved a 30–70% improvement in overall system performance. On the hold-out test set, FsGepa reached approximately 95% accuracy (with budget = 20), compared to an initial accuracy range of 55–75% using unoptimized prompts.

Since the sample tasks are relatively simple for modern LLMs, the baseline accuracy is already high, and performance tends to plateau quickly. Notably, while the initial accuracy shows high variance across runs, the optimized results stabilize around 95% accuracy, indicating convergence and robustness in the prompt optimization process.

## Implementation Notes
The FsGepa implementation aims to be faithful to the algorithms outlined in the paper but there are always differences between theory and implementation. Since there is no author-supplied implementation as of yet (10/'25), consider this as best-effort.

One important deviation between GEPA paper and FsGepa is the consideration of prompt templates. In many real-world use cases the prompts used are 'templates' with placeholder variables into which actual content is placed before execution. If a prompt template is evolved in the reflective update, there are special considerations for the template variables:
- They should be present exactly as-is in the evolved prompt.
- Their placement in the evolved prompt should make sense given the context.
- In most cases we want a single instance of the template variable in the generated prompt.

However, when used with the open-source gpt-oss-20b model, it was found that the above considerations were not met in the generated prompts - despite including specific instructions in the meta-prompt. To address, the generated prompts are post-processed to 'normalize' them so that they continue to be effective as prompt templates.


## Other Notes
Optimization requires access to GPU-based LLM models and may be costly due to the iterative nature of the optimization process (i.e. repeated invocations of the LLM model APIs). Substantial development was done with locally running GPU models 
