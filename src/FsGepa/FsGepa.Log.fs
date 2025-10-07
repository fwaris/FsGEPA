namespace FsGepa
open System
open Microsoft.Extensions.Logging

type Log = class end

module Log =
    ///allow for turning message loggin on/off at runtime
    let mutable debug_logging = false
    let mutable private _log : ILogger<Log> = LoggerFactory.Create(fun x -> x.AddConsole() |> ignore).CreateLogger<Log>()
    let info  (msg:string) = _log.LogInformation(msg)
    let warn (msg:string) = _log.LogWarning(msg)
    let error (msg:string) = _log.LogError(msg)
    let exn (exn:exn,msg) = _log.LogError(exn,msg)
    let trace (msg:string) = _log.LogTrace(msg)

    let init (sp:IServiceProvider) =
        match sp.GetService(typeof<ILoggerFactory>) with 
        | :? ILoggerFactory as l -> _log <- l.CreateLogger<Log>(); info "Initialized"
        | _ -> printfn "Logging factory not configured"

