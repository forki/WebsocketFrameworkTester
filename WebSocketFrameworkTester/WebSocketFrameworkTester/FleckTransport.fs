module FleckTransport

open Fleck
open System.Reactive.Subjects
open Transport
open System

let createFleckTransport (port: int) = 
    let fleckServer = new WebSocketServer((sprintf "ws://0.0.0.0:%i" port))

    let messageSubject = new Subject<_>()

    let onBinaryHandler conn (x: byte[]) = messageSubject.OnNext (conn, ReceivedByteMessage (ArraySegment(x)))
    let onStringMessage conn x = messageSubject.OnNext (conn, ReceivedStringMessage x)
    let onClose conn  = messageSubject.OnNext (conn, ConnectionClosed)
    let onError conn exn = messageSubject.OnNext (conn, Error exn)
    let onOpen conn = messageSubject.OnNext (conn, Open) 

    let websocketConnectionAction (webSocketConn: IWebSocketConnection) = 

        let connection = {
            new IServerClient with
                member x.Send msg = 
                    match msg with
                    | SendStringMessage(s) -> webSocketConn.Send(s) |> Async.AwaitIAsyncResult
                    | SendByteMessage(b) -> webSocketConn.Send(b.Array) |> Async.AwaitIAsyncResult
                    |> Async.Ignore
                member x.Flush() = async { return () }
            }
        
        webSocketConn.OnBinary <- (Action<_> (onBinaryHandler connection))
        webSocketConn.OnMessage <- (Action<_> (onStringMessage connection))
        webSocketConn.OnClose <- (Action (fun() -> onClose connection))
        webSocketConn.OnError <- (Action<_> (onError connection))
        webSocketConn.OnOpen <- (Action (fun () -> onOpen connection))

    fleckServer.Start(Action<_> websocketConnectionAction)

    { new IServerTransport with
        member x.InMessageObservable = messageSubject :> IObservable<_> 
        member x.Dispose() = fleckServer.Dispose(); messageSubject.Dispose(); }
