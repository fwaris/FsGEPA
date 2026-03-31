# FsGepa

FsGepa is an F# implementation of prompt optimization for compound AI systems. The repo includes the standard `GEPA` optimizer and a `VISTA` optimizer mode for hypothesis-driven reflective updates and restart behavior.

- [GEPA: Reflective Prompt Evolution Can Outperform Reinforcement Learning](https://arxiv.org/abs/2507.19457)
- [Project write-up](https://www.linkedin.com/posts/activity-7384785782890356736-uViM?utm_source=share&utm_medium=member_desktop&rcm=ACoAAAAbaagBCG-0LlGBjghxmo7KKzbEXRHmiZ0)

## Start here

The easiest way to understand the library is through the included samples:

- [FsgSample.Fvr](/src/FsgSample.Fvr/readme.md): a richer multi-step FEVEROUS claim-verification flow with multiple modules
- [FsgSample.Gsm8k](/src/FsgSample.Gsm8k/readme.md): a lighter-weight GSM8K benchmark sample for direct `GEPA` vs `VISTA` comparison

If you want the simplest benchmark harness first, start with the GSM8K sample. If you want to understand how to wire a more realistic multi-step system into FsGepa, start with the FEVEROUS sample.

## Overview

Automated prompt tuning can become expensive because optimization requires many repeated model calls. GEPA is designed to be relatively frugal while still improving prompt quality for a compound system rather than a single isolated prompt.

In FsGepa, a candidate system is a `GeSystem<'input,'output>`:

- it contains one or more prompt-bearing modules
- it exposes a `flow` function that runs the full task
- the flow may call external tools and may invoke one or more modules multiple times

Optimization operates over tasks, where each task contains:

- the task input
- an evaluation function that scores the resulting flow output
- optional feedback used during reflective updates

## Optimizer modes

FsGepa currently supports two optimizer modes:

- `GEPA`: reflective updates plus system-aware merges over a candidate pool
- `VISTA`: hypothesis-driven diagnosis and validation, with configurable restart behavior

Both modes share the same core abstractions and can optimize the same `GeSystem`.

## How GEPA works

Inspired by genetic algorithms, GEPA evolves new candidate systems from an existing population:

- In a reflective update, prompts are revised by reflecting on existing prompts together with sampled task inputs, outputs, feedback, and reasoning traces when available.
- In a system-aware merge, a new candidate is proposed by combining prompts from related candidates and their parent systems.

Important ideas carried into this implementation:

- `Pareto frontier`: candidate selection is based on quality-diversity over a fixed pareto task set rather than always picking the single best candidate
- `Reflection`: new candidates are guided by task-level feedback instead of relying only on few-shot exemplars
- `Frugality`: proposed candidates are screened on mini-batches before receiving more expensive evaluation

## Included benchmarks and samples

Two sample projects are included in the repo today:

- `src/FsgSample.Fvr`
  - based on FEVEROUS
  - demonstrates a multi-module flow that summarizes evidence and then classifies a claim
  - useful as an end-to-end example of wiring a realistic compound system into FsGepa
- `src/FsgSample.Gsm8k`
  - based on GSM8K
  - demonstrates a tighter exact-match benchmark for comparing `GEPA` and `VISTA`
  - includes both `defective` and `minimal` seeds modeled after the VISTA paper appendix

## Performance

The GEPA paper already establishes strong benchmark performance and includes ablation studies. This repo focuses on providing a practical F# implementation and runnable samples.

In the FEVEROUS sample, FsGepa can substantially improve holdout accuracy over the seed prompts with a modest budget. The GSM8K sample serves a different purpose: it gives a cleaner benchmark harness for comparing optimizer behavior under the same model, seed, and evaluation setup.

Results depend heavily on the backend model, prompt seed, and service stability, so sample documentation should be treated as directional rather than universal.

## Implementation notes

This implementation aims to stay faithful to the algorithms described in the papers, while still making practical engineering tradeoffs around transport, retries, telemetry, and concurrency.

One important operational setting is `flow_parallelism`, which is used as the per-run cap for concurrent outbound model calls. This matters especially when running against local or unstable backends.

## Practical notes

- You need access to one or more LLM backends, either local or hosted
- Optimization can be expensive because it repeatedly evaluates and rewrites prompts
- Local GPU-backed models are often the most convenient way to experiment cheaply
- The sample projects are the best reference for how to construct `Config`, tasks, systems, and evaluation logic
