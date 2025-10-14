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

- *Reflection*: The GEPA paper shows that reflection-tuned prompts generalize better (in addition to performing better overall) than the few-shot, exemplar based approach used by MIPROv2. The authors attribute this shift to improved instruction-following capability of newer models.

- *Frugality*: GEPA uses a small ('mini batch') sample of tasks (that are randomly selected from a larger set) to evaluate new candidate proposals. Only if the proposed candidate performs better than the donor parent, is it considered for evaluation on a the larger set. 

## Performance
The performance of GEPA is already established. The paper is comprehensive with several ablation studies demonstrating GEPA superiority (as-of 10/2025) over other considered approaches.

For the sample [included in this repo](/src/FsgSample.Fvr/readme.md)[FsgSample.Fvr](/src/FsgSample.Fvr/), FsGepa achieves ~90% accuracy on the hold out set, in contrast to the baseline accuracy of ~60% over the same (model=gpt-oss-20b). This is achieved with a budget of only 20 iterations. The sample selected is relatively simple. Its main purpose is to explain how to use FsGepa. Extensive setup (e.g. document indexes) is not required. Only two data files need be downloaded. 

## Implementation Notes
The FsGepa implementation aims to be faithful to the algorithms outlined in the paper but there are always differences between theory and implementation. Since there is no author-supplied implementation as of yet (10/'25), consider this as best-effort.

## Other Notes
Optimization requires access to GPU-based LLM models and may be costly due to the iterative nature of the optimization process (i.e. repeated invocations of the LLM model APIs). Substantial development was done with locally running GPU models 
