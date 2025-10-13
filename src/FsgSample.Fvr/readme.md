# FsgSample.Fvr
#### A sample FsGepa optimization set up.

## Overview and Setup
The sample is based on [Feverous task and dataset](https://fever.ai/dataset/feverous.html).

The task is to consider a *claim* and *supporting facts* and determine if the given facts SUPPORT / REFUTE the claim or there is NOT ENOUGH INFO. 

Fortunately, the Feverous team has created an easy-to-use task browser for us! Click [this link](html) to see an example task for better clarity.

There are two dataset required to run this sample:
- [Feverous development JSONL file](https://fever.ai/download/feverous/feverous_dev_challenges.jsonl)
- [Wikipedia article SQLLite Database](https://fever.ai/download/feverous/feverous-wiki-pages-db.zip)

The sample expects the following two files (available from the above links) in the user's 'Downloads' directory:

- feverous_dev_challenges.jsonl
- feverous_wikiv1.db
---
> #### Tools and Services Required: 
> - dotnet sdk 9.x
> - Visual Studio Code
> - Ionide F# VS Code Extension
> - Access to a suitable LLM (setup described later)
---

Once the data, tooling and service requirements are met, the solution can be compiled with the ```dotnet build``` command, run from the project root folder. Then from the *src/FsgSample.Fer* folder use ```dotnet run``` to run the sample. 

### FsGepa Configuration and LLM Service Access
FsGepa of course requires access to one or more LLMs, either through local services or through APIs.

To invoke FsGepa, the caller supplies a [Config](../FsGepa/Core.fs) instance. The properties of this structure are well documented in code comments. However one key property is `generate` which requires an instance of the [IGenerate](../FsGepa/Core.fs) interface. 

IGenerate is a highly abstracted interface for LLM response generation that FsGepa uses to make LLM calls - mostly for prompt updates.

A default IGenerate implementation is included that covers well-known service types (e.g. Chat Completions or Responses compatible APIs). See module [`FsgGenAI.GenAI`](../FsgGenAI/FsgGenAI.fs). You may supply a custom implementation, if necessary.

While an IGenerate instance is *required* for FsGepa, the same will usually also work for evaluation and scoring of the tasks (i.e. data) over which the prompts are optimized. 

The optimization process for FsgSample.Fvr is explained next. Use this as the basis for your own optimization set ups.

## Core Requirements for FsGepa Optimization

There four main items required to start FsGepa optimization.
1. Config structure with the required backend services (i.e. functional IGenerate).
2. Pareto tasks - a fixed list of tasks that are used to find the pareto frontier.
3. Feedback tasks - a sequence of tasks from which mini batches will be sampled to evaluate proposed new candidates.
4. Initial candidate *System* which will be evolved to find new candidates as the optimization progresses.

Config is already explained above. The other items are explained next.

The core types required to set up optimization are defined in [Core.fs](../FsGepa/Core.fs). They are generic and somewhat inter-related. These are explained first as the rest of the concepts are dependent on them.

> `GeSystem<'input,'output>` is a candidate *System* (as per GEPA paper). It holds the prompts that are to be optimized. The prompts are contained in *'modules'* (also a term from the paper). Additionally, `GeSystem<_,_>` has the *`flow`* function, which represents the *processing* i.e. take the `'input` and produce the `'output`. FsGepa does not impose any restrictions on `flow`; it may call external tools and/or invoke one or more modules, multiple times. 

> `GeTask<'input,'output>` holds the `'input` data and references an `evaluate` function which when given the `'input` and the `'output` (obtained from `flow`) produces a *score* [0..1] and optional feedback.

There are additional types are other details that are omitted to keep this explanation relatively understandable. 

## Feverous `Tasks`
For the Feverous setup, `'input` is bound to `FeverousResolved` - defined in [Data.fs](/src/FsgSample.Fvr/Data.fs). It contains the *claim*, *label* (ground truth) and the *supporting facts* formatted as a markdown document for easier LLM consumption. `FeverousResolved` instances are created by ingesting raw records from the JSONL file and combining them with the Wikipedia SQLLite text data. The processing is somewhat involved, see [Data.fs](/src/FsgSample.Fvr/Data.fs) for details.

The `'output` type is bound to `Answer` defined in [Tasks.fs](/src/FsgSample.Fvr/Tasks.fs). The `GeSystem<_,_>` `flow` function will convert the JSON response (*structured output*) form the LLM to an `Answer` instance. [Tasks.fs](/src/FsgSample.Fvr/Tasks.fs) also provides the logic for finding the pareto and feedback tasks, as described earlier, and the `evaluate` function associated with each `GeTask<FeverousResolved,Answer>` instance.

## Feverous `System`
[Opt.fs](/src/FsgSample.Fvr/Opt.fs) contains the construction of the initial `GeSystem<FeverousRecord,Answer>` instance that will serve as the root for the optimization run.

The `flow` function has two modules (or prompts).:
-  The first prompt takes the *claim* and *supporting facts* in the `'input` and produces a relevant *summary* of the facts. 
- The second prompt generates the `Answer` from the *claim* and the *summary*.

The two modules (with starer prompts) and the `flow` function are all defined in [Opt.fs](/src/FsgSample.Fvr/Opt.fs). FsGepa optimization is launched from the `start()` function also defined there.

## Optimization Run
After kick-off, optimization progress is relayed back to the caller via a telemetry channel (see [Config.fs](/src/FsGepa/Core.fs)). Events include new candidate *Systems* being added to the pool; new best prompts; etc. The events are defined in [Telemetry.fs](/src/FsGepa/Telemetry.fs). The caller can choose to take appropriate action (e.g. save the new best prompts as they are discovered).

The optimization process runs till the *budget* is exhausted. The high level flow is as follows:

1. Perform initialization
2. Determine the new pareto frontier - i.e., candidate `GeSystems<_,_>` instances that are dominant over other candidates in their performance over the pareto task set.
3. Propose a new candidate `GeSystem<_,_>` by either mutation (reflective update) or cross-over (system-aware merge)
4. Evaluate the proposed candidate over a mini batch sample of tasks drawn from the feedback task pool.
5. Also evaluate donor candidate (parent) over the same mini batch
6. If the proposed candidate performs better then its added to the candidate pool
7. Repeated from step 2. until budget is exhausted.

