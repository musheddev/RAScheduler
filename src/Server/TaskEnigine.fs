module rec Server.TaskEngine

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks
open System.Threading
open Microsoft.Extensions.Hosting
open Shared
open Cronos
open Server

type TaskFactory = Map<string, Arg> -> IServiceProvider -> Async<string>

let private engineTaskDb =
    ConcurrentDictionary<string, TaskFactory>()

let tryGetTaskFactory (name: string) =
    let mutable taskFactory = Unchecked.defaultof<TaskFactory>

    if engineTaskDb.TryGetValue(name, &taskFactory)
    then Some(taskFactory)
    else None

let tryAddTaskFactory (name: string) (factory: TaskFactory) = engineTaskDb.TryAdd(name, factory)

type TaskStatus =
    | Success of TimeSpan
    | Failed of exn
    | Canceled of bool

type TaskProcessorMessage =
    | Start of Start
    | Finished of Start * int * TaskStatus

type TaskTracking =
    { Task: Task
      CTS: CancellationTokenSource
      Start: Start }

type TaskProcessor(serviceProvider: IServiceProvider, stopToken: CancellationToken) =
    let cancellationTokenSource =
        CancellationTokenSource.CreateLinkedTokenSource(stopToken)

    let tasks =
        ConcurrentDictionary<int, TaskTracking>()

    let processorCompletion = TaskCompletionSource()

    let processorFn (msg, actor: ThreadedActor<TaskProcessorMessage>) =
        printfn "TP msg %O" msg

        match msg with
        | Start ((scheduleId, args) as start) ->

            try
                if (stopToken.IsCancellationRequested)
                then failwith "Task can't be added on canceled task processor"

                let tokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationTokenSource.Token)

                let fn () =
                    let taskId = Task.CurrentId.Value

                    Database.createTaskResult
                        { Start = DateTime.Now
                          Stop = DateTime.MaxValue
                          ScheduleId = scheduleId
                          RunId = taskId
                          Result = "" } |> ignore
                    //Run timer on task thread for metrics
                    let stopWatch = Stopwatch()
                    stopWatch.Start()

                    try
                        let workAsync =
                            Database.tryGetSchedule scheduleId
                            |> Option.bind (fun schedule -> tryGetTaskFactory schedule.TaskName)
                            |> Option.map (fun taskFactory -> taskFactory args serviceProvider)

                        if workAsync.IsSome then
                            let result =
                                Async.RunSynchronously(workAsync.Value, cancellationToken = tokenSource.Token)

                            Database.updateTaskResult scheduleId taskId (fun x ->
                                { x with
                                      Result = result
                                      Stop = DateTime.Now })

                            stopWatch.Stop()
                            actor.Enqueue(Finished(start, taskId, TaskStatus.Success stopWatch.Elapsed)) //starting on task thread
                        else
                            failwithf "Could not find schedule or task for id %i" scheduleId
                    with
                    | :? OperationCanceledException as e ->
                        let isTimeout =
                            stopToken.IsCancellationRequested
                            <> tokenSource.IsCancellationRequested

                        actor.Enqueue(Finished(start, taskId, TaskStatus.Canceled isTimeout))
                    | e -> actor.Enqueue(Finished(start, taskId, TaskStatus.Failed e))


                let task =
                    new Task(Action(fn),
                             tokenSource.Token,
                             TaskCreationOptions.LongRunning
                             ||| TaskCreationOptions.AttachedToParent)

                let success =
                    tasks.TryAdd
                        (task.Id,
                         { Start = start
                           Task = task
                           CTS = tokenSource })

                if not success
                then failwith "Task couldn't be added to tracker!"

                task.Start(TaskScheduler.Default) //send to default thread pool
            with e -> printfn "Failed to start task %O" e //Never started a task

        | Finished (start, id, status) ->
            let hasTrack, tracker = tasks.TryGetValue(id)

            match status with
            | Success (timespan) -> if tracker.Task.IsCompleted then ()
            | Failed (e) ->
                printfn "Task failure: %O" e

                Database.updateTaskResult (fst start) id (fun x ->
                    { x with
                          Stop = DateTime.Now
                          Result = sprintf "Task failure: %O" e })
            | Canceled (isTimeout) ->
                if isTimeout then
                    printfn "Task timed out or is to large: Start %O" start

                    Database.updateTaskResult (fst start) id (fun x ->
                        { x with
                              Stop = DateTime.Now
                              Result = sprintf "Task timed out or is to large: Start %O" start })
                else
                    printfn "Task canceled: Start %O" start

                    Database.updateTaskResult (fst start) id (fun x ->
                        { x with
                              Stop = DateTime.Now
                              Result = sprintf "Task canceled: Start %O" start })

            if hasTrack then
                tracker.Task.Dispose()
                tracker.CTS.Dispose()
                tasks.Remove(id) |> ignore

            if stopToken.IsCancellationRequested
               && tasks.Count = 0
               && actor.QueueCount = 0 then
                processorCompletion.SetCanceled()


    let processor =
        new ThreadedActor<TaskProcessorMessage>(processorFn)
    //For precaution against threading issues using dedicated thread for managing tasks and not the thread pool

    member this.StartTask(name, args) = processor.Enqueue(Start(name, args))

    member this.Count = tasks.Count

    member this.HealthProbe() =
        let probe =
            { Instant = DateTime.UtcNow
              RunningTasks =
                  tasks.Values
                  |> Seq.map (fun x -> x.Start)
                  |> Seq.toArray
              QueueLength = processor.QueueCount }

        probe

    member this.Cancel(id: int) =
        let hasTask, tracker = tasks.TryGetValue(id)
        tracker.CTS.Cancel()

    member this.AwaitCancel() =
        if stopToken.IsCancellationRequested
           && tasks.Count = 0
           && processor.QueueCount = 0 then
            processorCompletion.SetResult()

        processorCompletion.Task

    interface IDisposable with
        member this.Dispose() =
            for (tracker) in tasks.Values do
                tracker.Task.Dispose()
                tracker.CTS.Dispose()

            (processor :> IDisposable).Dispose()
            cancellationTokenSource.Dispose()
            processorCompletion.Task.Dispose()


//Simple object for sorting, with second level accuracy.
[<Struct>]
type TriggerPoint =
    { Seconds: int64 }

    static member OfDateTime(dateTime: DateTime) =
        let dto =
            dateTime.ToUniversalTime() |> DateTimeOffset

        { Seconds = dto.ToUnixTimeSeconds() }

    static member OfCron(cron: string) =
        CronExpression
            .Parse(cron)
            .GetNextOccurrence(DateTime.UtcNow)
        |> Option.ofNullable
        |> Option.map TriggerPoint.OfDateTime

    static member OfCronRange (toUtc: DateTime) (cron: string) =
        CronExpression
            .Parse(cron)
            .GetOccurrences(DateTime.UtcNow, toUtc)
        |> Seq.map TriggerPoint.OfDateTime
        |> Seq.toArray

    static member ToDateTime(triggerPoint: TriggerPoint) =
        DateTimeOffset
            .FromUnixTimeSeconds(triggerPoint.Seconds)
            .DateTime


type private TaskEngineMessage =
    | Schedules of Schedule []
    | Process
    | Cancel of int

type private ScheduleQueue() =
    let currentTriggers = Dictionary<TriggerPoint, Set<int>>()
    let schedules = Dictionary<int, Schedule>()

    member this.State() =
        seq {
            for KeyValue (triggerPoint, ids) in currentTriggers do
                for id in ids do
                    if schedules.ContainsKey(id)
                    then yield TriggerPoint.ToDateTime triggerPoint, schedules.Item(id)
        }
        |> Seq.toArray

    member this.Add(schedule: Schedule) =
        if schedule.Id.IsNone then failwith "Schedule without id"

        schedule.Id
        |> Option.iter (fun id ->
            if schedules.ContainsKey(id) then schedules.Item(id) <- schedule else schedules.Add(id, schedule)

            schedule.CronExpression
            |> TriggerPoint.OfCronRange(DateTime.UtcNow.AddDays(1.0))
            |> Array.iter (fun t ->
                if currentTriggers.ContainsKey(t)
                then currentTriggers.Item(t) <- currentTriggers.Item(t).Add(id)
                else currentTriggers.Add(t, id |> Set.singleton)))

    member this.PeekRange(currentTrigger) =
        let sorted =
            SortedSet<TriggerPoint>(currentTriggers.Keys) //given the infrequencey of calls resort the triggers everytime

        let items =
            sorted.GetViewBetween(sorted.Min, currentTrigger)
            |> Seq.toArray

        let response =
            items
            |> Seq.collect (fun trigger ->
                currentTriggers.Item(trigger)
                |> Seq.map (fun x -> schedules.Item x))
            |> Seq.toArray

        response

    member this.RemoveRange(currentTrigger) =
        let sorted =
            SortedSet<TriggerPoint>(currentTriggers.Keys)

        let items =
            sorted.GetViewBetween(sorted.Min, currentTrigger)
            |> Seq.toArray

        let ids =
            items
            |> Seq.collect (fun trigger -> currentTriggers.Item(trigger))
            |> Set.ofSeq

        for KeyValue (key, value) in currentTriggers do
            currentTriggers.Item(key) <- Set.difference (currentTriggers.Item(key)) ids

        for trigger in items do
            currentTriggers.Remove(trigger) |> ignore

        ()

    member this.RemoveById(id: int) =
        if schedules.ContainsKey(id) then schedules.Remove id |> ignore

        for KeyValue (key, value) in currentTriggers do
            if value.Contains id
            then currentTriggers.Item(key) <- currentTriggers.Item(key).Remove(id)


    member this.Head =
        currentTriggers.Item(this.HeadTrigger())
        |> Seq.map (fun x -> schedules.Item x)
        |> Seq.toArray

    member this.HeadTrigger() = currentTriggers.Keys |> Seq.min


type ScheduleProcessor(dispatch: Start -> unit, serviceProvider: IServiceProvider, cancellationToken: CancellationToken) =

    let queue = ScheduleQueue()

    let engine = //Ok to use thread pool here as it does not interact with tasks on a control level. (Managing one task from within another task is a recipe for deadlock)
        MailboxProcessor.Start(fun inbox ->
            async {

                use timer =
                    new System.Threading.Timer(TimerCallback(fun _ -> inbox.Post(Process)))

                let reset () =
                    let nextTime = queue.HeadTrigger().Seconds

                    let curTime =
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()

                    let timeout = int (nextTime - curTime)
                    printfn "Next trigger point time: %i" timeout

                    timer.Change(timeout * 1000, Timeout.Infinite)
                    |> ignore

                while true do
                    let! msg = inbox.Receive()
                    printfn "SP msg %O" msg

                    match msg with
                    | Schedules (schedules) ->
                        for schedule in schedules do
                            queue.Add(schedule)

                        reset ()
                    | Cancel (id) -> ()
                    | Process ->
                        let now = TriggerPoint.OfDateTime(DateTime.UtcNow)
                        let schedules = queue.PeekRange(now)

                        for schedule in schedules do
                            dispatch (schedule.Id.Value, schedule.Args |> Map.ofArray)

                        queue.RemoveRange(now)

                        for schedule in schedules do
                            queue.Add(schedule)

                        reset ()
            })

    member this.Queue(schedules: Schedule []) = engine.Post(Schedules(schedules))

    member this.Cancel(schedule: Schedule) =
        schedule.Id
        |> Option.iter (fun id -> engine.Post(Cancel(id)))

    member this.QueueState() = queue.State()


//dirty global to avoid complex di logic
let private _taskEngineService =
    ref Unchecked.defaultof<TaskEngineService>

let getTaskEngineService () = _taskEngineService.Value

type TaskEngineService(app: IHostApplicationLifetime, serviceProvider: IServiceProvider) =

    let cts = new CancellationTokenSource()
    let mutable taskProcessor = Unchecked.defaultof<TaskProcessor>
    let mutable scheduleProcessor = Unchecked.defaultof<ScheduleProcessor>

    member this.Status() =

        let hp = taskProcessor.HealthProbe()
        let qst = scheduleProcessor.QueueState()
        printfn "HP: %O" hp
        hp, qst

    member this.Queue(schedule) = scheduleProcessor.Queue([| schedule |])

    interface IHostedService with

        member this.StartAsync(ct) =
            printfn "Starting Task Engine"
            taskProcessor <- new TaskProcessor(serviceProvider, cts.Token)

            let dispatch = taskProcessor.StartTask
            scheduleProcessor <- new ScheduleProcessor(dispatch, serviceProvider, cts.Token)
            _taskEngineService := this //inject into dirty di
            Task.CompletedTask

        member this.StopAsync(ct) =
            cts.Cancel()
            taskProcessor.AwaitCancel()
