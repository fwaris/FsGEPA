namespace FsgSample.Fvr
open System

module Pgm = 
    [<EntryPoint>]
    let main args = 
        match args |> Array.toList with
        | "compare"::_ ->
            Opt.compare()
            |> Async.RunSynchronously
            |> ignore
        | _ ->
            Opt.start()
            |> Async.RunSynchronously
            |> ignore
            System.Console.WriteLine("Press <enter> to quit")
            System.Console.ReadLine() |> ignore
        0
