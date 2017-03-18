module SuaveTransport

open Suave
open Suave.Web
open Suave.Operators
open Suave.WebSocket
open Suave.Sockets.Control
open Suave.Http
open Suave.Filters
open Suave.Successful
open Transport
open System.Threading
open Suave.Sockets
open System

let createSuaveTransport port = 

    let receiveMessageSubject = Event<IServerClient * ServerReceivedMessage>()

    let websocketConnectionWebPart (ws: WebSocket) context = 
        let mutable loop = true
        let toSend = MailboxProcessor.Start (fun inbox -> async {
            let close = ref false
            while not !close do
                let! op,data,fi = inbox.Receive()
                let! _ = ws.send op data fi
                close := op = Close                    
        })

        let serverClient = {
            new IServerClient with
                member x.Send stcm = 
                    match stcm with
                    | ServerSentMessage.SendByteMessage(s) -> async { toSend.Post (Opcode.Binary, s, true) }
                    | ServerSentMessage.SendStringMessage(s) ->
                        let ba = System.Text.Encoding.UTF8.GetBytes(s)
                        async { toSend.Post (Opcode.Text, (ByteSegment(ba)), true) }
                member x.Flush() = async { return () }
            }

        receiveMessageSubject.Trigger(serverClient, ServerReceivedMessage.Open)

        socket {
            while loop do
                let! m = ws.read()
                match m with
                | Text,data,true -> 
                    receiveMessageSubject.Trigger(
                        serverClient, 
                        ServerReceivedMessage.ReceivedStringMessage(System.Text.Encoding.UTF8.GetString(data))) 
                | Binary,data,true -> 
                    receiveMessageSubject.Trigger(
                        serverClient, 
                        ServerReceivedMessage.ReceivedByteMessage(ByteSegment(data)))         
                | Ping,_,_ -> 
                    toSend.Post (Pong, (ByteSegment()), true)
                | Close,_,_ -> 
                    toSend.Post (Close, (ByteSegment()), true)
                    loop <- false
                | _ -> ()
        }

    let serverConfig = { 
        defaultConfig with 
            SuaveConfig.bindings = [ HttpBinding.create Protocol.HTTP (System.Net.IPAddress.Parse "0.0.0.0") port ] 
            SuaveConfig.tcpServerFactory = Suave.BasicTcpServerFactory() //Suave.DotNetty.DotNettyServerFactory()
            }

    let webPart = 
        choose [
            GET >=> pathScan "/test/%s" (fun x -> OK (sprintf "Hello! %s" x))
            handShake websocketConnectionWebPart 
            ]

    let (suaveServerStarted, suaveFinished) = startWebServerAsync serverConfig webPart
    let cancellationTokenSource = new CancellationTokenSource() 

    let listening, server = startWebServerAsync serverConfig webPart
    Async.Start(server, cancellationTokenSource.Token)
    listening |> Async.RunSynchronously |> ignore // wait for the server to start listening

    { new IServerTransport with
        member x.Dispose() = cancellationTokenSource.Cancel()
        member x.InMessageObservable = receiveMessageSubject.Publish :> IObservable<_> }


   


