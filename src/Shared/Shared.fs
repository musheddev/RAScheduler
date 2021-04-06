namespace Shared

open System

type Arg =
    | String of string
    | Int of int
    | DateTime of DateTime
    | Float of float

type Start = int * Map<string, Arg>

type HealthProbe =
    { Instant: DateTime
      RunningTasks: Start []
      QueueLength: int }

type Schedule =
    { CronExpression: string
      TaskName: string
      Id: int option
      Args: (string * Arg) [] }

type TaskResult =
    { ScheduleId: int
      RunId: int
      Start: DateTime
      Stop: DateTime
      Result: string }

module Route =
    let checkChronExpression = "/api/checkchronexpression"

    let schedule = "/api/schedule"
