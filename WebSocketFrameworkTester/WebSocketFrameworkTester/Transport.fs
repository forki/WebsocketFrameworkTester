module Transport

open System

type ServerReceivedMessage = 
    | ReceivedStringMessage of string
    | ReceivedByteMessage of ArraySegment<byte>
    | ConnectionClosed
    | Error of exn
    | Open

type ServerSentMessage =  
    | SendStringMessage of string
    | SendByteMessage of ArraySegment<byte>

type IServerClient =
    abstract member Send: ServerSentMessage -> Async<unit>
    abstract member Flush: unit -> Async<unit>
 
type IServerTransport = 
    inherit IDisposable
    abstract member InMessageObservable: IObservable<IServerClient * ServerReceivedMessage> with get

type SendClientMessage = 
    | ByteMessage of ArraySegment<byte>
    | StringMessage of string

type IClient = 
    inherit IDisposable
    abstract member Send: SendClientMessage -> unit
    abstract member ReceivedMessages: IObservable<ServerSentMessage> with get
