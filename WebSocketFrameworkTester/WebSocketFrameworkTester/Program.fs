module Program

open Transport
open WebSocketSharp
open System
open System.Threading.Tasks
open FSharp.Control.Reactive
open System.Reactive.Concurrency

let byteDataToTestSize = 8000
let stringToSend = "TESTESTSERVER"
let amountOfTimesToSend = 50000
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
    websocket.OnClose |> Event.add (fun _ -> closed <- true)
    async {
        let! openSub = websocket.OnOpen |> Async.AwaitEvent |> Async.StartChild
        let! errorSub = websocket.OnError |> Async.AwaitEvent |> Async.StartChild
        do websocket.Connect()
        let! connResult = waitForFirstAsync openSub errorSub
        match connResult with
        | Choice1Of2(success) -> ()
        | Choice2Of2(err) -> failwith err.Message
        }
    |> Async.RunSynchronously

    let receivedStringEvent = websocket.OnMessage |> Event.filter (fun x -> x.IsText) |> Event.map (fun x -> ServerSentMessage.SendStringMessage(x.Data))
    let receivedBinaryEvent = websocket.OnMessage |> Event.filter (fun x -> not (x.IsText)) |> Event.map (fun x -> SendByteMessage(ArraySegment(x.RawData)))
    let messagesObservable = receivedStringEvent |> Event.merge receivedBinaryEvent

    { new IClient with
        member x.Dispose() = websocket.Close()
        member x.Send(ssm) = 
            match ssm with
            | SendClientMessage.ByteMessage(b) -> 
                // Doesn't support byte segments
                let newArrayToSend = b.Array.[ b.Offset .. (b.Offset + b.Count) ]
                websocket.Send(newArrayToSend)
            | StringMessage(s) -> websocket.Send(s)
        member x.ReceivedMessages = messagesObservable :> IObservable<_> }

open System.Net.WebSockets
let createDotNetWebsocketClient port = 
    let wsClient = new ClientWebSocket()
    let cancellationToken = new System.Threading.CancellationTokenSource()
    async {
        let connectTask = wsClient.ConnectAsync((System.Uri(sprintf "ws://localhost:%i" port)), cancellationToken.Token) 
        return! connectTask |> Async.AwaitTask
        
    }
    |> Async.RunSynchronously
    |> ignore
    
    { new IClient with 
        member x.Dispose() = wsClient.Dispose() 
        member x.Send(ssm) = 
            async {
                let ct = Async.DefaultCancellationToken
                let sendTask = 
                    match ssm with
                    | SendClientMessage.ByteMessage(b) -> wsClient.SendAsync(b, WebSocketMessageType.Binary, true, ct)
                    | SendClientMessage.StringMessage(s) -> wsClient.SendAsync((ArraySegment(System.Text.Encoding.UTF8.GetBytes(s))), WebSocketMessageType.Text, true, ct)
                return! sendTask |> Async.AwaitTask
            }
            |> Async.RunSynchronously
        member x.ReceivedMessages = 
            let subscriberLogic (observer: IObserver<_>) = 
                let buffer = Array.zeroCreate (byteDataToTestSize * 3)
                let mutable finished = false
                while (not finished) do
                   try
                       async {
                            let! ct = Async.CancellationToken
                            let! receiveResult = wsClient.ReceiveAsync((ArraySegment<_>(buffer)), ct) |> Async.AwaitTask
                            let toSend = 
                                match receiveResult.MessageType with
                                | WebSocketMessageType.Binary -> Some (ServerSentMessage.SendByteMessage(ArraySegment(buffer, 0, receiveResult.Count)))
                                | WebSocketMessageType.Text -> 
                                    let s = System.Text.Encoding.UTF8.GetString(buffer, 0, receiveResult.Count)
                                    Some (ServerSentMessage.SendStringMessage(s))
                                | WebSocketMessageType.Close -> None
                                | _ -> failwith "No case"
                            match toSend with | Some(x) -> observer.OnNext(x) | None -> ()
                            }
                       |> Async.RunSynchronously
                   with
                   | ex -> finished <- true; observer.OnError(ex)
                { new IDisposable with member __.Dispose() = () }
            System.Reactive.Linq.Observable.Create(subscriberLogic) }

let runClientTest port = 
    //let client = createWebsocketClient port 
    let client = TcpTransport.createClient (System.Net.IPAddress.Loopback) port 

    let mutable result = true
    let count = ref 0
    let finishedEvent = Event<_>()

    let stopWatch = System.Diagnostics.Stopwatch()

    let checkingSub = 
        client.ReceivedMessages
        |> Observable.subscribeOn Scheduler.Default
        |> Observable.subscribe (fun x -> 
            let nc =  System.Threading.Interlocked.Increment(count)
            (*
            match x with
            | ServerSentMessage.SendByteMessage(b) -> 
                printfn "Byte message received %i" nc
            | ServerSentMessage.SendStringMessage(s) -> printfn "String message received [Message %s, Count: %i]" s nc
            *)
            if nc = amountOfTimesToSend then count := 0; finishedEvent.Trigger())
    
    let rec test continueRunning = 
        async {
            let! eventWaiting = finishedEvent.Publish |> Async.AwaitEvent |> Async.StartChild
            stopWatch.Start()
            do client.Send (SendClientMessage.StringMessage("TESTTEST"))
            do! eventWaiting
            stopWatch.Stop()
            printfn "Test finished [Time taken milliseconds: %i; AmountOfTimes: %i, Update per sec: %f]" stopWatch.ElapsedMilliseconds amountOfTimesToSend ((float stopWatch.ElapsedMilliseconds) / (float amountOfTimesToSend))
            stopWatch.Reset()
            if continueRunning then return! test false else return ()
        }

    test true
    |> Async.RunSynchronously

[<EntryPoint>]
let main argv = 

    let port = 9001

    //use server = FleckTransport.createFleckTransport port
    //use server = SuaveTransport.createSuaveTransport (uint16 port)
    use server = TcpTransport.createServer port

    let messageHandler = 
        server.InMessageObservable
        |> Observable.subscribe (fun (cc, message) -> 
            let rec runAsync currentCount = async {
                if currentCount >= amountOfTimesToSend
                then return ()
                else 
                    do! cc.Send(ServerSentMessage.SendStringMessage(stringToSend))
                    return! runAsync (currentCount + 1)
                }
            runAsync 0 |> Async.Start
            )
    
    runClientTest port
    // runClientTest port // Run again due to JIT

    0
