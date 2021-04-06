# RA Scheduler
A simple task scheduler. The client site was abandoned early on in favor of integration tests.

## Install pre-requisites
You'll need to install the following pre-requisites in order to build SAFE applications

* The [.NET](https://www.microsoft.com/net/download) 5.0
* [npm](https://nodejs.org/en/download/) package manager.
* [Node LTS](https://nodejs.org/en/download/).

## Starting the application
Start the server:
```bash
cd src\Server\
dotnet run
```

Run the integration tests (takes 1 min):

```bash
cd Test
dotnet run
```

Start the client (abandoned):

```bash
npm install
npm run start
```

Open a browser to `http://localhost:8080` to view the site.

## Important Dependencies
* [Saturn](https://saturnframework.org/docs/)
* [Fable](https://fable.io/docs/)
* [Elmish](https://elmish.github.io/elmish/)
* [FsHttp](https://github.com/ronaldschlenker/FsHttp)
* [Expecto](https://github.com/haf/expecto)
* [Giraffe](https://github.com/giraffe-fsharp/Giraffe)
* [FSharp.SystemTextJson](https://github.com/Tarmil/FSharp.SystemTextJson)
