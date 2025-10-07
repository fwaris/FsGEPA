namespace FsgHotpot
open System
open FSharp.Data
open FsGepa

module Opt = 

    type THotQA = JsonProvider<Samples.TYPE_SAMPLE> 
    let file = home @@ "Downloads" @@ "hotpot_dev_distractor_v1.json" 

    let data = lazy(
        use str = IO.File.OpenRead file
        THotQA.Load(str)
    )


    let evaluate (cfg:Config) (input:string) = async {
        return ""
    }


    let toTask (j:THotQA.Root) = ()
        