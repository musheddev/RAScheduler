module Index

open System
open Elmish
open Thoth.Fetch
open Fulma
open Shared

type Model =
    { Error: string
      CronInput: string
      UrlArgInput: string
      Schedules: Schedule []
      TaskResults: TaskResult []
      Status: (HealthProbe * (DateTime * Schedule) []) option }

type Msg =
    | CheckChronExpression of string
    | CreateSchedule of string * string
    | GotSchedules of Schedule []
    | GotTaskResults of TaskResult []
    | GotStatus of HealthProbe * (DateTime * Schedule) []

let init () =
    let model: Model =
        { Error = ""
          CronInput = ""
          UrlArgInput = ""
          Schedules = [||]
          TaskResults = [||]
          Status = None }

    let getHello () =
        Fetch.get<unit, string> Route.checkChronExpression
    //let cmd = Cmd.OfPromise.perform getHello () GotHello
    model, Cmd.none

let update msg model =
    match msg with
    | _ -> model, Cmd.none

open Fable.React
open Fable.React.Props

let view model dispatch =
    div [ Style [ TextAlign TextAlignOptions.Center
                  Padding 40 ] ] [
        div [] [
            img [ Src "favicon.png" ]
            h1 [] [ str "RAScheduler" ]

            h2 [] [ str "CreateTask" ]
            input [ Type "text" ]
            button [ OnClick(fun mevent -> dispatch (CheckChronExpression "")) ] [
                str "Check Chron Expression"
            ]
        ]
    ]
