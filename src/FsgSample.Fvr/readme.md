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


> #### Tools and Services Required: 
> - dotnet sdk 9.x
> - Visual Studio Code
> - Ionide F# VS Code Extension
> - Access to a sutiable LLM (setup described later)


Once the data, tooling and service requirements are met, the solution can be compiled with the ```dotnet build``` command, run from the project root folder. Then from the *src/FsgSample.Fer* folder use ```dotnet run``` to run the sample. 

### LLM Setup









