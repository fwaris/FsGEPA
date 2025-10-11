#load "../../FsGepa/Template.fs"
let str = """
Your task:  
Given the claim `{$claim}` and the source text `{$content}`, locate every sentence or fragment in `{$content}` that provides evidence for the claim.  
Extract those facts, summarize them concisely (bullet‑point style), and indicate the level of support (fully, partially, or not supported) based on the facts extracted.  
Do not add external information or commentary.  If no facts support the claim, state that explicitly.  
Use the following format:

- Fact: <supporting sentence or paraphrase>  
- Fact: <another supporting sentence or paraphrase>  

Support Level: <Full / Partial / Not Supported>
"""

let normStr = FsGepa.Template.normalizePrompt str |> printfn "%s"


