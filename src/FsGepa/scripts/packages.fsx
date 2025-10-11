#r "nuget: FSharp.Data"
#r "nuget: Parquet.Net, 5.3.0-pre.3"
open System
open System.IO
open System.Net.Http

[<AutoOpen>]
module Utls = 
    let home = Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)

    let (@@) (a:string) (b:string) = Path.Combine(a,b)


