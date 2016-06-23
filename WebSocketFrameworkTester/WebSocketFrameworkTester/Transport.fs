module Transport

open System

type ServerReceivedMessage = 
    | ReceivedStringMessage of string
    | ReceivedByteMessage of byte[]
    | ConnectionClosed
    | Error of exn
    | Open

type ServerSentMessage =  
    | SendStringMessage of string
    | SendByteMessage of byte[]

type IServerClient =
    abstract member Send: ServerSentMessage -> unit
 
type IServerTransport = 
    inherit IDisposable
    abstract member InMessageObservable: IObservable<IServerClient * ServerReceivedMessage> with get

type SendClientMessage = 
    | ByteMessage of byte[]
    | StringMessage of string

type IClient = 
    inherit IDisposable
    abstract member Send: SendClientMessage -> unit
    abstract member ReceivedMessages: IObservable<ServerSentMessage> with get
