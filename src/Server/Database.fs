module Server.Database

open Shared
open System.Collections.Concurrent
open System
//using light memory db for this example
//would most likely be a service in real world
let private schedule_db = ConcurrentDictionary<int, Schedule>()

let listSchedules () = schedule_db.Values |> Seq.toArray

let tryGetSchedule (id: int) =
    let exists, value = schedule_db.TryGetValue(id)
    if exists then Some(value) else None

let tryAddSchedule (schedule: Schedule) =
    //thread safe way of generate id and adding to concurrent dictionary
    let mutable next_id =
        if schedule_db.Keys |> Seq.isEmpty then 0 else 1 + (schedule_db.Keys |> Seq.max)

    let mutable newSchedule = { schedule with Id = Some(next_id) }

    while not (schedule_db.TryAdd(next_id, newSchedule)) do
        next_id <- 1 + (schedule_db.Keys |> Seq.max)
        newSchedule <- { schedule with Id = Some(next_id) }

    newSchedule


let tryDeleteSchedule (scheduleId: int) =
    let mutable schedule = Unchecked.defaultof<Schedule>
    schedule_db.TryRemove(scheduleId, &schedule)



let private task_db =
    ConcurrentDictionary<int, TaskResult list>()

let createTaskResult (taskResult: TaskResult) =
    task_db.AddOrUpdate(taskResult.ScheduleId, [ taskResult ], (fun _ results -> taskResult :: results))

let updateTaskResult (scheduleId: int) (runId: int) (fn: TaskResult -> TaskResult) =
    if task_db.ContainsKey(scheduleId) then
        let applyChanges =
            List.map (fun taskResult ->
                if taskResult.Stop = DateTime.MaxValue
                   && taskResult.RunId = runId then
                    fn taskResult
                else
                    taskResult)

        let newTaskResults = task_db.Item(scheduleId) |> applyChanges

        task_db.AddOrUpdate(scheduleId, newTaskResults, (fun _ taskResults -> applyChanges taskResults))
        |> ignore

let getTaskResults (scheduleId: int) =
    if task_db.ContainsKey scheduleId then task_db.Item(scheduleId) |> List.toArray else [||]
