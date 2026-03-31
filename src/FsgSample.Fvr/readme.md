# FsgSample.Fvr

`FsgSample.Fvr` is the more involved example sample in the repo. It uses the FEVEROUS dataset to show how to optimize a multi-step claim-verification system with FsGepa.

If you want a lighter benchmark focused on direct `GEPA` vs `VISTA` comparison, see [FsgSample.Gsm8k](/src/FsgSample.Gsm8k/readme.md). If you want to understand how to wire a realistic multi-module flow into FsGepa, this is the better starting point.

## Overview

The task is to take:

- a claim
- supporting facts gathered from Wikipedia

and predict whether the evidence:

- `SUPPORTS`
- `REFUTES`
- `NOT ENOUGH INFO`

The FEVEROUS team provides an interactive task browser, which is useful for getting familiar with the dataset shape. See the dataset site here:

- [Feverous task and dataset](https://fever.ai/dataset/feverous.html)

## Data setup

This sample requires two downloaded files:

- [Feverous development JSONL file](https://fever.ai/download/feverous/feverous_dev_challenges.jsonl)
- [Wikipedia article SQLite database](https://fever.ai/download/feverous/feverous-wiki-pages-db.zip)

The sample expects these files in `~/Downloads`:

- `feverous_dev_challenges.jsonl`
- `feverous_wikiv1.db`

## Tooling

- `dotnet` SDK
- an F#-capable editor such as VS Code with Ionide
- access to a suitable LLM backend

## Running the sample

Run from the repo root.

### Compare mode

Compare mode computes a shared holdout baseline and then runs both `GEPA` and `VISTA` over the same initial system:

```bash
dotnet run --project src/FsgSample.Fvr -- compare
```

The comparison output reports:

- baseline score
- optimized score
- improvement
- candidate count
- elapsed time
- winner

### Standalone mode

If you run the sample without `compare`, it starts the standalone optimization entry point:

```bash
dotnet run --project src/FsgSample.Fvr
```

At the moment, this path uses the VISTA GPT-OSS configuration selected in `Opt.fs` and streams telemetry to the console.

## Environment variables

### Backend selection

- `FSGEPA_LLAMACPP_ENDPOINT`
  - default: `http://localhost:8081/v1`
- `FSGEPA_LLAMACPP_MODEL`
  - default: `gpt-oss-20b-mxfp4.gguf`

### Compare mode

- `FSGEPA_COMPARE_BUDGET`
- `FSGEPA_COMPARE_MINI_BATCH`
- `FSGEPA_COMPARE_PARETO`
- `FSGEPA_COMPARE_FEEDBACK`
- `FSGEPA_COMPARE_HOLDOUT`

Example:

```bash
env \
  FSGEPA_COMPARE_BUDGET=2 \
  FSGEPA_COMPARE_PARETO=12 \
  FSGEPA_COMPARE_FEEDBACK=8 \
  FSGEPA_COMPARE_HOLDOUT=12 \
  dotnet run --project src/FsgSample.Fvr -- compare
```

## FsGepa configuration and LLM access

To invoke FsGepa, the caller supplies a [`Config`](../FsGepa/Core.fs) instance. One key field is `generator`, which is an implementation of [`IGenerate`](../FsGepa/Core.fs).

`IGenerate` is the abstraction FsGepa uses for outbound model calls, primarily for prompt updates and reflective reasoning. A default implementation is included for common API styles in [`FsGepa.GenAI.Api`](../FsGepa.GenAI/FsGepa.GenAI.fs), but you can also provide your own.

In many setups, the same backend used for optimizer reflection is also used by the sample’s `flow` during task execution.

## Core requirements for optimization

There are four main ingredients required to run optimization:

1. A `Config` instance with working backend access
2. A fixed pareto task set
3. A feedback task pool used for minibatch evaluation
4. An initial candidate `GeSystem<'input,'output>`

The core types used to express this are defined in [`Core.fs`](../FsGepa/Core.fs).

- `GeSystem<'input,'output>`
  - a candidate system containing prompt-bearing modules plus the `flow` function that processes the task end to end
- `GeTask<'input,'output>`
  - a task input together with the evaluation function that scores a `FlowResult`

## FEVEROUS tasks in this sample

For this setup, the input type is `FeverousResolved`, defined in [Data.fs](/src/FsgSample.Fvr/Data.fs). It contains:

- the claim
- the ground-truth label
- the supporting facts formatted for model consumption

These values are built by combining the raw JSONL records with the Wikipedia SQLite data.

The output type is `Answer`, defined in [Tasks.fs](/src/FsgSample.Fvr/Tasks.fs). The sample flow converts the model response into this output type, and the same module also contains the task construction and evaluation logic.

## The system being optimized

The initial system is built in [Opt.fs](/src/FsgSample.Fvr/Opt.fs).

This flow contains two modules:

- a summarization prompt that reads the claim and supporting facts and produces a concise evidence summary
- a decision prompt that predicts the FEVEROUS label from the claim and summary

This makes the sample useful as a reference for multi-module prompt optimization rather than only single-prompt tuning.

## Optimization flow

Once started, optimization progress is emitted through telemetry events defined in [`Telemetry.fs`](../FsGepa/Telemetry.fs). These events include updates such as newly accepted candidates, new best prompts, and frontier changes.

At a high level the loop is:

1. Initialize the candidate pool
2. Determine the current pareto frontier
3. Propose a new candidate through reflection, merge, or VISTA-style diagnosis depending on the active optimizer mode
4. Evaluate the proposal on a minibatch from the feedback pool
5. Compare it against the relevant parent candidate on the same minibatch
6. Accept it into the pool if it improves
7. Repeat until the budget is exhausted

## Notes

- This sample is more realistic than the GSM8K sample because it exercises a multi-step flow
- The GSM8K sample is usually a better benchmark when you want a clearer optimizer-to-optimizer comparison signal
- Backend reliability matters a lot for long runs, especially when working with local models
