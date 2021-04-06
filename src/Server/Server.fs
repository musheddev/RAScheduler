namespace Server

open Giraffe

open System.Text.Json
open System.Text.Json.Serialization
open Giraffe.Serialization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Saturn
open Cronos
open Shared
open FSharp.Control.Tasks
open System
open Server.TaskEngine
open Thoth.Json.Giraffe
open Thoth.Json.Net


type SystemTextJsonSerializer(options: JsonSerializerOptions) =
    interface IJsonSerializer with
        member _.Deserialize<'T>(string: string) =
            JsonSerializer.Deserialize<'T>(string, options)

        member _.Deserialize<'T>(bytes: byte []) =
            JsonSerializer.Deserialize<'T>(ReadOnlySpan bytes, options)

        member _.DeserializeAsync<'T>(stream) =
            JsonSerializer
                .DeserializeAsync<'T>(stream, options)
                .AsTask()

        member _.SerializeToBytes<'T>(value: 'T) =
            JsonSerializer.SerializeToUtf8Bytes<'T>(value, options)

        member _.SerializeToStreamAsync<'T> (value: 'T) stream =
            JsonSerializer.SerializeAsync<'T>(stream, value, options)

        member _.SerializeToString<'T>(value: 'T) =
            JsonSerializer.Serialize<'T>(value, options)

module Server =

    let checkChronExpression next (ctx: HttpContext) =
        task {
            let! expression = ctx.ReadBodyFromRequestAsync()
            let cronExpression = CronExpression.Parse(expression)

            let nextOccurrence =
                cronExpression
                    .GetNextOccurrence(DateTime.UtcNow)
                    .Value.ToString("o")

            return! Successful.ok (text (nextOccurrence)) next ctx
        }

    let createSchedule next (ctx: HttpContext) =
        task {
            let! schedule = ctx.BindModelAsync<Schedule>()

            let cronExpression =
                CronExpression.Parse(schedule.CronExpression)

            let newSchedule = Database.tryAddSchedule schedule
            let taskEngine = getTaskEngineService ()
            taskEngine.Queue(newSchedule)
            return! Successful.created (json newSchedule) next ctx
        }

    let listSchedules next ctx =
        let schedules = Database.listSchedules ()
        Successful.ok (json schedules) next ctx


    let tryGetSchedule (id: int) =
        match (Database.tryGetSchedule id) with
        | Some (schedule) -> Successful.OK(json schedule)
        | None -> RequestErrors.notFound (text "")


    let getTaskResults (id: int) =
        let taskResults = Database.getTaskResults id
        printfn "%A" taskResults
        Successful.ok (json taskResults)

    let healthProbe next ctx =
        let status = getTaskEngineService().Status()
        Successful.ok (json status) next ctx



    let webApp =
        router {
            post Route.checkChronExpression checkChronExpression
            put Route.schedule createSchedule
            get Route.schedule listSchedules
            getf "/api/schedule/%i" tryGetSchedule
            getf "/api/schedule/%i/taskresult" getTaskResults
            get "/api/healthprobe" healthProbe
        }


    let errorHandler (ex: Exception) (logger: ILogger) =
        logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")

        clearResponse
        >=> ServerErrors.INTERNAL_ERROR ex.Message

    let configureApp (webHostBuilder: IWebHostBuilder) =
        webHostBuilder
            .UseKestrel()
            .UseWebRoot("public")
            .UseUrls([| "http://*:8080" |])
            .Configure(fun app ->
                app
                    .UseStaticFiles()
                    .UseGiraffeErrorHandler(errorHandler)
                    .UseGiraffe(webApp))
        |> ignore

    [<EntryPoint>]
    let main _ =
        Host
            .CreateDefaultBuilder()
            .ConfigureServices(fun (services: IServiceCollection) ->
                let jsonOptions = JsonSerializerOptions()
                jsonOptions.Converters.Add(JsonFSharpConverter())
                TaskEngine.tryAddTaskFactory "ScanHeadersTask" HtmlHeadersTask.ScanHeadersTask |> ignore //TODO: taskEngine db should be service

                services
                    .AddGiraffe()
                    .AddSingleton(jsonOptions)
                    .AddSingleton<IJsonSerializer, SystemTextJsonSerializer>()
                    .AddHostedService<TaskEngineService>()
                |> ignore)
            .ConfigureWebHostDefaults(Action<IWebHostBuilder> configureApp)
            .Build()
            .Run()

        0
