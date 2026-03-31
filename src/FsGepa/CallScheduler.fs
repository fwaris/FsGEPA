namespace FsGepa

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

type private ICallWork =
    abstract member Execute : unit -> Async<unit>

type internal ICallScheduler =
    abstract member Schedule<'t> : label:string -> work:(unit -> Async<'t>) -> Async<'t>

type private ScheduledCall<'t>(work:unit -> Async<'t>, tcs:TaskCompletionSource<'t>) =
    interface ICallWork with
        member _.Execute() = async {
            try
                let! result = work()
                tcs.TrySetResult result |> ignore
            with ex ->
                tcs.TrySetException ex |> ignore
        }

type internal IScheduledGenerate = interface end

type private ScheduledGenerateWrapper(scheduler:ICallScheduler, inner:IGenerate) =
    interface IGenerate with
        member _.generate(model:Model) (systemMessage:string option) (messages:GenMessage list) (responseFormat:Type option) (opts:GenOpts option) =
            scheduler.Schedule $"model:{model.id}" (fun () ->
                inner.generate model systemMessage messages responseFormat opts)

    interface IScheduledGenerate

module internal CallScheduler =

    let private awaitValueTask (valueTask: ValueTask) =
        valueTask.AsTask() |> Async.AwaitTask

    let private awaitValueTaskResult<'t> (valueTask: ValueTask<'t>) =
        valueTask.AsTask() |> Async.AwaitTask

    let start (maxInFlight:int) (queueCapacity:int) : ICallScheduler * IAsyncDisposable =
        let workerCount = max 1 maxInFlight
        let queueSize = max 1 queueCapacity
        let options = BoundedChannelOptions(queueSize)
        options.FullMode <- BoundedChannelFullMode.Wait
        options.SingleReader <- false
        options.SingleWriter <- false
        let channel = Channel.CreateBounded<ICallWork>(options)
        let mutable inFlight = 0
        let mutable queued = 0

        let workerLoop () = async {
            let reader = channel.Reader
            while! reader.WaitToReadAsync() |> awaitValueTaskResult do
                let! item = reader.ReadAsync() |> awaitValueTaskResult
                Interlocked.Decrement(&queued) |> ignore
                Interlocked.Increment(&inFlight) |> ignore
                try
                    do! item.Execute()
                finally
                    Interlocked.Decrement(&inFlight) |> ignore
        }

        let workers =
            [| for _ in 1 .. workerCount -> Async.StartAsTask(workerLoop()) :> Task |]

        let scheduler =
            { new ICallScheduler with
                member _.Schedule<'t>(label:string) (work:unit -> Async<'t>) = async {
                    let tcs = TaskCompletionSource<'t>(TaskCreationOptions.RunContinuationsAsynchronously)
                    let item = ScheduledCall<'t>(work, tcs) :> ICallWork
                    let queuedNow = Interlocked.Increment(&queued)
                    try
                        if not (channel.Writer.TryWrite item) then
                            Log.info $"Call scheduler saturated; waiting to enqueue {label}. queued={queuedNow}, in_flight={Volatile.Read(&inFlight)}"
                            do! channel.Writer.WriteAsync(item) |> awaitValueTask
                        return! tcs.Task |> Async.AwaitTask
                    with ex ->
                        Interlocked.Decrement(&queued) |> ignore
                        return raise ex
                } }

        let disposer =
            { new IAsyncDisposable with
                member _.DisposeAsync() =
                    let disposeTask = task {
                        channel.Writer.TryComplete() |> ignore
                        do! Task.WhenAll(workers)
                    }
                    ValueTask disposeTask
            }

        scheduler, disposer

module internal ScheduledGenerate =

    let wrap (scheduler:ICallScheduler) (inner:IGenerate) =
        ScheduledGenerateWrapper(scheduler, inner) :> IGenerate

module internal ScheduledRun =

    let withScheduledGenerator cfg work = async {
        match cfg.generator with
        | :? IScheduledGenerate ->
            return! work cfg
        | _ ->
            let scheduler, disposer =
                CallScheduler.start cfg.flow_parallelism cfg.flow_parallelism
            let cfg' = { cfg with generator = ScheduledGenerate.wrap scheduler cfg.generator }
            let! result = work cfg' |> Async.Catch
            do! disposer.DisposeAsync().AsTask() |> Async.AwaitTask
            match result with
            | Choice1Of2 value -> return value
            | Choice2Of2 ex -> return raise ex
    }
