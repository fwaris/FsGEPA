namespace FsGepa
open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module Template = 
    // Pattern to match single brace {$name} (to be replaced with double braces)
    let singleBraceRegex = Regex(@"\{\s*\$([a-zA-Z][a-zA-Z0-9_]*)\s*\}", RegexOptions.Compiled)

    // Pattern to match {{ $variableName }} with optional whitespace
    let templateVarRegex = Regex(@"\{\{\s*\$([a-zA-Z][a-zA-Z0-9_]*)\s*\}\}", RegexOptions.Compiled)

    // Pattern to match {[NAME]} format (LLM generated tags)
    let bracketBraceRegex = Regex(@"\{\s*\[\s*([a-zA-Z][a-zA-Z0-9_]*)\s*\]\s*\}", RegexOptions.Compiled)

    // Step 1: Normalize single braces to double braces and {[NAME]} to [NAME]
    let private normalizeBraces (text: string) =
        let normalized1 = singleBraceRegex.Replace(text, "{{$$$1}}")
        // Normalize {[NAME]} to [NAME] with uppercase
        bracketBraceRegex.Replace(normalized1, fun (m: Match) -> 
            $"[{m.Groups.[1].Value.ToUpper()}]"
        )

    let extractVarNames (text: string) =
        templateVarRegex.Matches(text)
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> Seq.toList

    // Step 2: Extract unique variable names
    let extractUniqueVarNames (text: string) =
        extractVarNames text |> List.distinct

    // Step 3 & 4: Replace all instances with placeholders and add mapping at the end
    let normalizePrompt (prompt: string) =
        // Step 1: Normalize
        let normalized = normalizeBraces prompt
        
        // Step 2: Get unique variable names
        let varNames = extractUniqueVarNames normalized
        
        // Step 3: Replace all double-braced variables with [varname]
        let withPlaceholders = 
            varNames
            |> List.fold (fun text varName ->
                let pattern = Regex.Escape($"{{{{${varName}}}}}") |> Regex
                pattern.Replace(text, $"[{varName.ToUpper()}]")
            ) normalized
        
        // Step 4: Add mapping at the end
        let mappings = 
            varNames
            |> List.map (fun varName -> $"[{varName.ToUpper()}]\n{{{{${varName}}}}}")
            |> String.concat "\n"
        
        if varNames.IsEmpty then
            withPlaceholders
        else
            $"{withPlaceholders}\n\n{mappings}"

    (* Result:
    // Example usage:
    let prompt = "Hello {$name}, welcome to {{$city}}. Your name is {{$name}} and city is {$city}."
    let result = normalizePrompt prompt

    Hello [name], welcome to [city]. Your name is [name] and city is [city].

    [name] = {{$name}}
    [city] = {{$city}}
    *)