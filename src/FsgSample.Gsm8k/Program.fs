namespace FsgSample.Gsm8k
open System

module Pgm =
    [<EntryPoint>]
    let main args =
        let seed =
            args
            |> Array.tryFind (fun arg -> arg.Equals("defective", StringComparison.OrdinalIgnoreCase) || arg.Equals("minimal", StringComparison.OrdinalIgnoreCase))
            |> SeedKind.parse
        let mode =
            args
            |> Array.tryFind (fun arg -> arg.Equals("gepa", StringComparison.OrdinalIgnoreCase) || arg.Equals("vista", StringComparison.OrdinalIgnoreCase) || arg.Equals("both", StringComparison.OrdinalIgnoreCase))
            |> CompareMode.parse

        match args |> Array.toList with
        | "compare"::_ ->
            Opt.compare seed mode
            |> Async.RunSynchronously
            |> ignore
        | _ ->
            Opt.start seed
            |> Async.RunSynchronously
            |> ignore
            Console.WriteLine("Press <enter> to quit")
            Console.ReadLine() |> ignore
        0
