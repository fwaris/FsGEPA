# FsgSample.Gsm8k

`FsgSample.Gsm8k` is a GSM8K benchmark sample for comparing `GEPA` and `VISTA` on a simple math-word-problem task. It was added to give the repo a benchmark with a clearer quality signal than the earlier FEVEROUS sample, and it mirrors the two seed styles discussed in the VISTA paper appendix:

- `defective`: a verbose seed that asks for step-by-step work and a `final_answer`
- `minimal`: a compact seed that only asks for a JSON object containing `final_answer`

Both optimizers start from the same seed prompt and optimize the single `solve` module prompt.

## What the sample does

- Loads official GSM8K `train` and `test` JSONL files
- Converts each record into a `question`, normalized `final_answer`, and stored worked solution
- Deterministically shuffles the data with `Random(0)`
- Splits the training set into:
  - `pareto` tasks used during search
  - `feedback` tasks used to generate optimizer feedback
- Uses the test set as a holdout set for the reported baseline and optimized scores
- Scores exact-match on normalized final answers

Normalization removes formatting noise such as `$`, commas, spacing, and simple numeric formatting differences before comparing answers.

## Data setup

Place these files in `~/Downloads`:

- `gsm8k_train.jsonl`
- `gsm8k_test.jsonl`

The sample expects the official JSONL format from the `openai/grade-school-math` dataset.

Current code paths:

- training data: `~/Downloads/gsm8k_train.jsonl`
- test data: `~/Downloads/gsm8k_test.jsonl`

## Running the sample

Run from the repo root:

```bash
dotnet run --project src/FsgSample.Gsm8k -- compare minimal
```

That command runs the comparison mode with the `minimal` seed and, by default, executes both `GEPA` and `VISTA`.

### Compare mode

Compare mode computes one shared baseline on the holdout set, then runs one or both optimizers from the same initial seed.

Examples:

```bash
dotnet run --project src/FsgSample.Gsm8k -- compare minimal
dotnet run --project src/FsgSample.Gsm8k -- compare minimal gepa
dotnet run --project src/FsgSample.Gsm8k -- compare minimal vista
dotnet run --project src/FsgSample.Gsm8k -- compare defective
dotnet run --project src/FsgSample.Gsm8k -- compare defective vista
```

The output includes:

- shared baseline holdout score
- optimized holdout score
- improvement over baseline
- candidate count
- elapsed time
- winner when both optimizers are run

### Standalone start mode

If you omit `compare`, the sample runs the standalone optimizer entry point:

```bash
dotnet run --project src/FsgSample.Gsm8k -- minimal
dotnet run --project src/FsgSample.Gsm8k -- defective
```

This path currently runs a VISTA-configured optimization pass with telemetry enabled and is useful for ad hoc experimentation.

## GEPA vs. VISTA in this sample

`GEPA` and `VISTA` both optimize the same solver prompt, but they use different search behavior:

- `GEPA` uses the standard GEPA optimizer mode
- `VISTA` uses `VistaMode` with a paper-aligned default preset in code:
  - `hypothesis_count = 3`
  - `hypotheses_to_validate = 3`
  - `epsilon_greedy = 0.10`
  - `random_restart_stagnation = None`
  - `random_restart_probability = 0.20`

The sample is therefore a good place to compare optimizer behavior while keeping the task, model, and evaluation fixed.

## Environment variables

### Backend selection

- `FSGEPA_LLAMACPP_ENDPOINT`
  - default: `http://localhost:8081/v1`
- `FSGEPA_LLAMACPP_MODEL`
  - default: `gpt-oss-20b-mxfp4.gguf`
- `FSGEPA_FLOW_PARALLELISM`
  - default: `5`
  - this is the per-run cap for concurrent outbound model calls

### Compare mode settings

- `FSGEPA_GSM8K_COMPARE_BUDGET`
- `FSGEPA_GSM8K_COMPARE_MINI_BATCH`
- `FSGEPA_GSM8K_COMPARE_PARETO`
- `FSGEPA_GSM8K_COMPARE_FEEDBACK`
- `FSGEPA_GSM8K_COMPARE_HOLDOUT`
- `FSGEPA_GSM8K_COMPARE_BASELINE_OVERRIDE`

Example:

```bash
env \
  FSGEPA_GSM8K_COMPARE_BUDGET=5 \
  FSGEPA_GSM8K_COMPARE_HOLDOUT=200 \
  FSGEPA_FLOW_PARALLELISM=5 \
  dotnet run --project src/FsgSample.Gsm8k -- compare minimal vista
```

### Standalone start mode settings

- `FSGEPA_GSM8K_START_PARETO`
- `FSGEPA_GSM8K_START_FEEDBACK`
- `FSGEPA_GSM8K_START_HOLDOUT`

## Notes

- The sample expects the model to return JSON containing `final_answer`, but it also includes fallback parsing to recover from imperfect output formatting.
- Because this benchmark uses exact-match final-answer scoring, it is much easier to tell whether a prompt change helped or hurt than on noisier generation tasks.
- When the backend is unstable, retries may recover from some failures, but lower `FSGEPA_FLOW_PARALLELISM` values have been more reliable in practice.
