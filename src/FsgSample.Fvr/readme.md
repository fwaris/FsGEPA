# FsgSample.Fvr
#### Sample to demonstrate FsGepa Usage

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

There is a default IGenerate implementation [FsgGenAI.GenAI module](../FsgGenAI/FsgGenAI.fs) included that covers the well-known service types (e.g. Chat Completions or Responses compatible APIs). Supply a custom implementation, if necessary.

While an IGenerate instance is required for FsGepa, the same will usually also work for evaluation and scoring of the tasks (data) over which the prompts are optimized. 

The optimization process for FsgSample.Fvr is explained next. Use this as the basis for your own optimization task.




















