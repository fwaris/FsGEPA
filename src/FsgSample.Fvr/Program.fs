namespace FsgSample.Fvr
open System

module Pgm = 
    [<EntryPoint>]
    let main args = 
        let st = ref Unchecked.defaultof<_>
        async {
            let! st' = Opt.start()
            st.Value <- st'
        }
        |> Async.Start
        System.Console.WriteLine("Press <enter> to quit")
        System.Console.ReadLine() |> ignore
        0