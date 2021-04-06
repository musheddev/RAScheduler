namespace Server

open Shared

module Scraper =

    open System.Net
    open System
    open HtmlAgilityPack
    open System.Text
    open Microsoft.FSharp.Control.WebExtensions

    let fetchAsync (url: string) =
        async {
            let uri = new System.Uri(url)
            let webClient = new WebClient()
            let! html = webClient.AsyncDownloadString(uri)
            return html, webClient.ResponseHeaders
        }

    let scrapePageHeaders (url: string) = //TODO: save result to result db instead of returning string.

        async {
            printfn "Running Scrape Headers Task"

            let! content, responseHeaders = fetchAsync url
            if (String.IsNullOrEmpty content) then failwithf "Empty content at url %s" url
            let document = HtmlAgilityPack.HtmlDocument()
            document.LoadHtml(content)

            let metaHeaders =
                document
                    .DocumentNode
                    .SelectSingleNode("//head")
                    .ChildNodes
                |> Seq.filter (fun t -> t.Name = "meta")
                |> Seq.map (fun t -> t.WriteTo())
                |> Seq.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat Environment.NewLine

            let pageTitle =
                let title =
                    document.DocumentNode.SelectSingleNode("//title")

                if title = null then "" else title.InnerText

            return pageTitle + Environment.NewLine + metaHeaders
        }

module HtmlHeadersTask =

    open Server.TaskEngine

    let ScanHeadersTask: TaskFactory =
        fun args provider ->
            match args.TryFind "url" with
            | Some (Arg.String (s)) -> Scraper.scrapePageHeaders s
            | _ ->
                async {
                    failwithf "Invalid Args: %O" args
                    return ""
                }
