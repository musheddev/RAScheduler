namespace Server

open System
open System.Collections.Concurrent
open System.Threading

module ActorStatus =

    [<Literal>]
    let Idle = 0L

    [<Literal>]
    let Occupied = 1L

    [<Literal>]
    let Stopped = 2L

/// <summary> An actor pulls a message off of a (thread-safe) queue and executes it in the isolated context of the actor. </summary>
/// <typeparam name = "tmsg"> the type of messages that this actor will manage </typeparam>
/// <param name = "bodyFn"> the function to execute when the thread is available </param>
type ThreadedActor<'tmsg>(bodyFn: 'tmsg * ThreadedActor<'tmsg> -> unit) as this =

    let mutable status: int64 = ActorStatus.Idle

    //Do not expose these
    let signal = new ManualResetEventSlim(false)
    let queue = ConcurrentQueue<'tmsg>()

    // This is the main thread function (delegate). It gets executed when the thread is started/signaled
    let threadFn () =
        // While the actor is not stopped, check the queue and process any awaiting messages
        while Interlocked.Read(&status) <> ActorStatus.Stopped do
            while not queue.IsEmpty do
                // If the thread is idle, update it to 'occupied'
                Interlocked.CompareExchange(&status, ActorStatus.Occupied, ActorStatus.Idle)
                |> ignore
                // Try to get the next message in the queue
                let isSuccessful, message = queue.TryDequeue()
                // If we successfully retrieved the next message, execute it in the context of this thread
                if isSuccessful then bodyFn (message, this)
            // If the thread is 'occupied', mark it as idle
            Interlocked.CompareExchange(&status, ActorStatus.Idle, ActorStatus.Occupied)
            |> ignore
            // Reset the thread signal
            signal.Reset()
            // Tell the thread to block until it gets signalned for more work
            signal.Wait()
        // If the thread is stopped, dispose of it
        signal.Dispose()

    // The thread associated with this actor (one-to-one relationship)
    // Pass the threadFn delegate to the constructor
    let thread =
        Thread(ThreadStart(threadFn), IsBackground = true, Name = "ActorThread")

    // Start the thread
    do thread.Start()

    /// Enqueue a new messages for the thread to pick up and execute
    member this.Enqueue(msg: 'tmsg) =
        if Interlocked.Read(&status) <> ActorStatus.Stopped then
            queue.Enqueue(msg)
            signal.Set()
        else
            failwith "Cannot queue to stopped actor."

    // Get the length of the actor's message queue
    member this.QueueCount = queue.Count

    // Stops the actor
    member this.Stop() =
        Interlocked.Exchange(&status, ActorStatus.Stopped)
        |> ignore

        signal.Set()

    interface IDisposable with
        member __.Dispose() =
            this.Stop()
            thread.Join()
