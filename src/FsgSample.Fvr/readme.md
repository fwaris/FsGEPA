# FsgSample.Fvr
#### Sample to demonstrate FsGepa Usage

## Overview and Setup
The sample is based on [Feverous task and dataset](https://fever.ai/dataset/feverous.html).

The task is to consider a *claim* and *supporting facts* and determine if the given facts SUPPORT / REFUTE the claim or there is NOT ENOUGH INFO. 

Fortunately, the Feverous team has created an easy-to-use task browser for us! Click [this link](html) to see an example task for better clarity.

There are two dataset required to run this sample:
- [Feverous development JSONL file](https://fever.ai/download/feverous/feverous_dev_challenges.jsonl)
- [Wikipedia article SQLLite Database](https://fever.ai/download/feverous/feverous-wiki-pages-db.zip)

The sample expects the following two files in the user 'Downloads' directory:

- feverous_dev_challenges.jsonl
- feverous_wikiv1.db

Tools Required: 
- dotnet sdk 9.x
- Visual Studio Code
- 



