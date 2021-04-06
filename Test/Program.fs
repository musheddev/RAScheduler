// Learn more about F# at http://fsharp.org

open System.Globalization
open System.Net
open System
open System.Text.Json.Serialization

open System.Threading
open Microsoft.FSharp.Control.WebExtensions

open FsHttp
open FsHttp.DslCE
open FsHttp.Testing
open Expecto
open Shared
open System.Threading.Tasks
open System.Text.Json

let host = "http://localhost:8080"
let options = JsonSerializerOptions()
do options.Converters.Add(JsonFSharpConverter())

let integration_tests =
    let schedule =
        { CronExpression = "* * * * *"
          TaskName = "ScanHeadersTask"
          Id = None
          Args = [| ("url", Arg.String("http://www.google.com")) |] }

    printfn "%s"
    <| JsonSerializer.Serialize(schedule, options)

    testSequenced
    <| testList
        "integration tests"
           [ test "check cron expression" {
                 let response =
                     http {
                         POST(host + Route.checkChronExpression)
                         body
                         text "* * * * *"
                     }

                 let dateTime =
                     response
                     |> Response.toString Int32.MaxValue
                     |> (fun s ->
                         printfn "%s" s
                         DateTime.Parse(s, null, DateTimeStyles.RoundtripKind))

                 Expect.equal response.statusCode HttpStatusCode.OK "Status code should be 200"
                 Expect.isLessThanOrEqual dateTime (DateTime.UtcNow.AddMinutes(1.0)) "Time should be close to 1 min"
             }

             test "create schedule" {
                 let response =
                     http {
                         PUT(host + Route.schedule)
                         CacheControl "no-cache"
                         body

                         json """
                    {"CronExpression":"* * * * *","TaskName":"ScanHeadersTask","Id":null,"Args":[["url",{"Case":"String","Fields":["http://www.google.com"]}]]}
                    """
                     }

                 Expect.equal response.statusCode HttpStatusCode.Created "Status code should be 201"
             }

             test "retrieve schedules" {
                 let response =
                     http {
                         GET(host + Route.schedule + "/0")
                         CacheControl "no-cache"
                     }
                     |> Response.toString Int32.MaxValue
                     |> JsonSerializer.Deserialize

                 Expect.equal response { schedule with Id = Some(0) } "should find new schedule"
             }

             test "retrieve results" {
                 let response =
                     http {
                         GET(host + Route.schedule + "/0/taskresult")
                         CacheControl "no-cache"
                     }
                     |> Response.toString Int32.MaxValue

                 let taskResults =
                     JsonSerializer.Deserialize<TaskResult []>(response, options)

                 Expect.hasLength taskResults 0 "Nothing should have run by now"
             }

             test "check health probe" {
                 let response =
                     http {
                         GET(host + "/api/healthprobe")
                         CacheControl "no-cache"
                     }
                     |> Response.toString Int32.MaxValue

                 let (healthProbe, scheduleQueue) =
                     JsonSerializer.Deserialize<HealthProbe * ((DateTime * Schedule) [])>(response, options)

                 Expect.hasLength scheduleQueue 1440 "default"
             }

             test "retrieve results after 1 min" {
                 Thread.Sleep(60000)

                 let response =
                     http {
                         GET(host + Route.schedule + "/0/taskresult")
                         CacheControl "no-cache"
                     }
                     |> Response.toString Int32.MaxValue

                 printfn "%s" response

                 let taskResults =
                     JsonSerializer.Deserialize<TaskResult []>(response, options)

                 Expect.hasLength taskResults 1 "1 result should have come in"
             } ]

let tests = testList "tests" [ integration_tests ]

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv tests |> ignore
    0 // return an integer exit code
