#load "packages.fsx"
#load "../../FsGepa/Utils.fs"
#load "../Data.fs"

open FsgIF

// Load data
let feverousRecords = Data.loadFeverous Data.FEVEROUS

// Get a sample record
let sampleRecord = feverousRecords |> Array.tryHead

match sampleRecord with
| Some record ->
    printfn "Testing generateDoc for record ID: %d" record.id
    printfn "Claim: %s" record.claim
    printfn "\n---\n"
    
    let doc = Data.generateDoc record
    printfn "%s" doc
    
    // Optionally save to file
    let outputPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads", "feverous_sample_doc.md")
    System.IO.File.WriteAllText(outputPath, doc)
    printfn "\n---\n"
    printfn "Document saved to: %s" outputPath
    
| None ->
    printfn "No records found"
