#load "packages.fsx"
#load "../../FsGepa/Utils.fs"
#load "../Data.fs"

open FsgIF

// Load data
let feverousRecords = Data.loadFeverous Data.FEVEROUS

// Get the record with ID 9770 which has a table caption
let sampleRecord = feverousRecords |> Array.find (fun r -> r.id = 9770)

printfn "Testing generateDoc for record ID: %d" sampleRecord.id
printfn "Claim: %s" sampleRecord.claim
printfn "\n---\n"

let doc = Data.generateDoc sampleRecord
printfn "%s" doc

// Save to file
let outputPath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads", "feverous_with_caption.md")
System.IO.File.WriteAllText(outputPath, doc)
printfn "\n---\n"
printfn "Document saved to: %s" outputPath
