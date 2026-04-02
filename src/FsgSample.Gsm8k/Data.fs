namespace FsgSample.Gsm8k
open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open FsGepa

type Gsm8kRecord = {
    [<JsonPropertyName("question")>]
    question : string
    [<JsonPropertyName("answer")>]
    answer : string
}

type Gsm8kInput = {
    id : string
    question : string
    final_answer : string
    worked_solution : string
}

module Data =
    let GSM8K_TRAIN = home @@ "Downloads" @@ "gsm8k_train.jsonl"
    let GSM8K_TEST = home @@ "Downloads" @@ "gsm8k_test.jsonl"

    let jsonOptions =
        let options = JsonSerializerOptions()
        options.PropertyNameCaseInsensitive <- true
        options

    let loadRecords path : Gsm8kRecord[] =
        if File.Exists path then
            File.ReadLines path
            |> Seq.filter (String.IsNullOrWhiteSpace >> not)
            |> Seq.map (fun line -> JsonSerializer.Deserialize<Gsm8kRecord>(line, jsonOptions))
            |> Seq.toArray
        else
            [||]

    let private answerRegex = Regex(@"####\s*(.+)$", RegexOptions.Compiled ||| RegexOptions.Multiline)
    let private numberRegex = Regex(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled)

    let normalizeFinalAnswer (text:string) =
        let trimmed =
            text.Trim()
                .Replace("$", "")
                .Replace(",", "")
                .Replace(" ", "")
        match Decimal.TryParse(trimmed, Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture) with
        | true, value ->
            value.ToString("0.############################", Globalization.CultureInfo.InvariantCulture)
        | _ ->
            let matches = numberRegex.Matches(trimmed)
            if matches.Count = 1 then
                let value = matches.[0].Value
                match Decimal.TryParse(value, Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture) with
                | true, number ->
                    number.ToString("0.############################", Globalization.CultureInfo.InvariantCulture)
                | _ ->
                    trimmed.ToLowerInvariant()
            else
                trimmed.ToLowerInvariant()

    let extractFinalAnswer (answer:string) =
        let matchResult = answerRegex.Match(answer)
        if matchResult.Success then
            matchResult.Groups.[1].Value |> normalizeFinalAnswer
        else
            answer |> normalizeFinalAnswer

    let toInput splitName index (record:Gsm8kRecord) =
        {
            id = $"{splitName}-{index + 1}"
            question = record.question.Trim()
            final_answer = extractFinalAnswer record.answer
            worked_solution = record.answer.Trim()
        }

    let loadTrainingInputs () =
        loadRecords GSM8K_TRAIN
        |> Array.mapi (toInput "train")

    let loadTestInputs () =
        loadRecords GSM8K_TEST
        |> Array.mapi (toInput "test")
