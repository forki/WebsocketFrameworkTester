module Program

open Transport
open WebSocket4Net
open System
open System.Threading.Tasks
open FSharp.Control.Reactive
open System.Reactive.Concurrency

let byteDataToTestSize = 80 * 1024 * 1024
let amountOfTimesToSend = 20
let byteDataToSend =
    let a = Array.zeroCreate byteDataToTestSize
    for i = 0 to byteDataToTestSize - 1 do
        a.[i] <- byte (i % 255)
    a

let waitForFirstAsync async1 async2 = async {
    let firstTask = async1 |> Async.StartAsTask 
    let secondTask = async2 |> Async.StartAsTask
    let index = Task.WaitAny (([| firstTask :> Task; secondTask :> Task |]), -1)
    return
        if index = 0 
            then Choice1Of2 firstTask.Result
            else Choice2Of2 secondTask.Result
    }

let createWebsocketClient port = 

    let mutable closed = false

    let websocket = new WebSocket((sprintf "ws://localhost:%i/" port))
    websocket.Closed |> Event.add (fun _ -> closed <- true)
    async {
        let! openSub = websocket.Opened |> Async.AwaitEvent |> Async.StartChild
        let! errorSub = websocket.Error |> Async.AwaitEvent |> Async.StartChild
        do websocket.Open()
        let! connResult = waitForFirstAsync openSub errorSub
        match connResult with
        | Choice1Of2(success) -> ()
        | Choice2Of2(err) -> raise err.Exception
        }
    |> Async.RunSynchronously

    let receivedStringEvent = websocket.MessageReceived |> Event.map (fun x -> ServerSentMessage.SendStringMessage(x.Message))
    let receivedBinaryEvent = websocket.DataReceived |> Event.map (fun x -> SendByteMessage(x.Data))
    let messagesObservable = receivedStringEvent |> Event.merge receivedBinaryEvent

    { new IClient with
        member x.Dispose() = websocket.Dispose()
        member x.Send(ssm) = 
            match ssm with
            | SendClientMessage.ByteMessage(b) -> websocket.Send(b, 0, b.Length)
            | StringMessage(s) -> websocket.Send(s)
        member x.ReceivedMessages = messagesObservable :> IObservable<_> }

let runClientTest port = 
    let client = createWebsocketClient port 

    let mutable result = true
    let count = ref 0
    let finishedEvent = Event<_>()

    let checkingSub = 
        client.ReceivedMessages
        |> Observable.subscribeOn Scheduler.Default
        |> Observable.subscribe (fun x -> 
            match x with
            | ServerSentMessage.SendByteMessage(b) -> 
                let c = System.Threading.Interlocked.Increment(count)
                printfn "Sending %i update" c
                if result = true then result <- byteDataToSend = b
            | ServerSentMessage.SendStringMessage(_) -> result <- false)

    async {
        let! eventWaiting = finishedEvent.Publish |> Async.AwaitEvent |> Async.StartChild
        do client.Send (SendClientMessage.StringMessage("TESTTEST"))
        do! eventWaiting
    }
    |> Async.RunSynchronously

    result

[<EntryPoint>]
let main argv = 

    let port = 9000

    //use server = FleckTransport.createFleckTransport port
    use server = SuaveTransport.createSuaveTransport (uint16 port)

    let messageHandler = 
        server.InMessageObservable
        |> Observable.subscribe (fun (cc, message) -> 
            for i = 1 to amountOfTimesToSend do
                cc.Send(ServerSentMessage.SendByteMessage(byteDataToSend))
            )
    
    let result = 
        Async.Parallel (seq { for i = 1 to Environment.ProcessorCount do yield async { return runClientTest port } })
        |> Async.RunSynchronously


    if result |> Array.forall id
       then 
            printfn "Test failed"
            1 
       else 0
