module TcpTransport

open System
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Threading
open System.IO
open Transport
open System.Reactive.Linq

let headerLength = 1 + sizeof<int32>

//let readingSemaphore = new System.Threading.SemaphoreSlim(1, 1)


let asyncRead (stream: Stream) (byteSegment: ArraySegment<byte>) byteCountToRead = 
    if byteCountToRead > byteSegment.Count then failwith "Bytes to read requested greater than buffer size"
    let rec readLoop currentOffset leftoverCount = async {
       // do! readingSemaphore.WaitAsync() |> Async.AwaitTask
        let! bytesRead = stream.ReadAsync(byteSegment.Array, currentOffset, leftoverCount) |> Async.AwaitTask
        //readingSemaphore.Release() |> ignore
        let newCount = leftoverCount - bytesRead
        if newCount = 0
        then return ()
        else return! readLoop (currentOffset + bytesRead) newCount
        }
    
    readLoop byteSegment.Offset byteCountToRead

let asyncWrite (stream: Stream) (byteSegment: ArraySegment<byte>) = stream.WriteAsync(byteSegment.Array, byteSegment.Offset, byteSegment.Count) |> Async.AwaitTask

let createServer port = 

    let receiveMessageSubject = Event<IServerClient * ServerReceivedMessage>()

    let ipAddress = IPAddress.Any
    let endpoint = IPEndPoint(ipAddress, port)

    let cts = new CancellationTokenSource()

    let writingSemaphore = new System.Threading.SemaphoreSlim(1, 1)
    let socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
    socket.Bind(endpoint)
    socket.Listen(int SocketOptionName.MaxConnections)

    let rec serverConnLoop (observer: IObserver<_>) = 
        async {
            let! acceptedConn = Async.FromBeginEnd(socket.BeginAccept, socket.EndAccept)

            let remoteClientInfo = acceptedConn.RemoteEndPoint;
            printfn "Client connected on %O" remoteClientInfo
            let readStream = new BufferedStream(new NetworkStream(acceptedConn, false), 100000)
            let writeStream = new BufferedStream(new NetworkStream(acceptedConn, false), 100000)
            let mutable clientHeaderBuffer = Array.zeroCreate headerLength
            let mutable serverToClientBuffer = Array.zeroCreate 2000 
            let serverClient = {
                new IServerClient with 
                    member x.Flush() = writeStream.FlushAsync() |> Async.AwaitTask
                    member x.Send serverSentMsg = 
                        match serverSentMsg with
                        | ServerSentMessage.SendByteMessage byteSegment -> 
                            async {
                                do! writingSemaphore.WaitAsync() |> Async.AwaitTask
                                do! asyncWrite writeStream ([| 0uy |] |> ArraySegment)
                                do! asyncWrite writeStream (BitConverter.GetBytes(byteSegment.Count) |> ArraySegment)
                                do! asyncWrite writeStream byteSegment
                                writingSemaphore.Release() |> ignore }
                        | SendStringMessage str -> 
                            async {
                                  try
                                        let lengthRequired = System.Text.Encoding.UTF8.GetMaxByteCount(str.Length)
                                        if serverToClientBuffer.Length < lengthRequired then serverToClientBuffer <- Array.zeroCreate lengthRequired 
                                        let bytesWritten = System.Text.Encoding.UTF8.GetBytes(str, 0, str.Length, serverToClientBuffer, 0)
                                        do! writingSemaphore.WaitAsync() |> Async.AwaitTask
                                        do! asyncWrite writeStream ([| 1uy |] |> ArraySegment)
                                        do! asyncWrite writeStream (BitConverter.GetBytes(bytesWritten) |> ArraySegment)
                                        do! asyncWrite writeStream (ArraySegment(serverToClientBuffer, 0, bytesWritten))
                                        writingSemaphore.Release() |> ignore
                                  with
                                  | ex -> 
                                      printfn "Error sending: %s %s" ex.Message ex.StackTrace
                                      raise ex }   
                }

            let mutable charToStringArray = Array.zeroCreate 2000
            let mutable bufferByteArray = Array.zeroCreate 200
            let messageTypeAndHeaderArray = Array.zeroCreate headerLength

            let rec receiveLoop() = async {
                do! asyncRead readStream (ArraySegment(messageTypeAndHeaderArray)) headerLength
                let length = BitConverter.ToInt32(messageTypeAndHeaderArray, 1)
                match messageTypeAndHeaderArray.[0] with 
                // Byte Message
                | 0uy -> 
                    do! asyncRead readStream (ArraySegment(bufferByteArray)) length
                    observer.OnNext(serverClient, (ReceivedByteMessage (ArraySegment(bufferByteArray, 0, length))))
                | 1uy -> 
                    if charToStringArray.Length < length then charToStringArray <- Array.zeroCreate length

                    do! asyncRead readStream (ArraySegment(charToStringArray)) length
                    let string = System.Text.Encoding.UTF8.GetString(charToStringArray, 0, length)
                    observer.OnNext(serverClient, (ServerReceivedMessage.ReceivedStringMessage string))
                | _ -> failwith "Invalid flag for message type received on protocol"
                        
                do! receiveLoop()
                }
           do! receiveLoop()
        }

    { new IServerTransport with
       member __.InMessageObservable = 
            Observable.Create(fun (observer: IObserver<_>) -> 
                let cancelSource = new CancellationTokenSource()
                Async.Start((serverConnLoop observer), cancelSource.Token)
                { new IDisposable with member x.Dispose() = cancelSource.Cancel() }
                )
        member __.Dispose() = socket.Dispose(); cts.Cancel(); cts.Dispose()
        }

let createClient (address: IPAddress) port = 

    let tcpClient = new TcpClient()
    tcpClient.Connect(address, port)
    let networkStream = tcpClient.GetStream()
    let readStream = new BufferedStream(networkStream, 100000)
    let writeStream = new BufferedStream(networkStream, 100000)
    let messageTypeAndLengthArrayForSending = Array.zeroCreate headerLength
    let messageTypeAndLengthArrayForReading = Array.zeroCreate headerLength
    let mutable incomingBuffer = Array.zeroCreate 2000
    let mutable charIncomingBuffer = Array.zeroCreate 2000
    let mutable toSendBuffer : byte array = Array.zeroCreate 2000

    let rec clientLoop (observer: IObserver<_>) = async {
        let! header = asyncRead readStream (ArraySegment(messageTypeAndLengthArrayForReading)) headerLength
        let length = BitConverter.ToInt32(messageTypeAndLengthArrayForReading, 1)

        match messageTypeAndLengthArrayForReading.[0] with
        | 0uy -> 
            if incomingBuffer.Length < length then incomingBuffer <- Array.zeroCreate length
            let! bytesRead = asyncRead readStream (ArraySegment(incomingBuffer)) length
            observer.OnNext(ServerSentMessage.SendByteMessage (ArraySegment(incomingBuffer, 0, length)))
        | 1uy ->
            if charIncomingBuffer.Length < length then charIncomingBuffer <- Array.zeroCreate (length)
            do! asyncRead readStream (ArraySegment(charIncomingBuffer)) length
            let stringResult = System.Text.Encoding.UTF8.GetString(charIncomingBuffer, 0, length)
            observer.OnNext(SendStringMessage stringResult)
        | _ -> failwith "Unexpected protocol when receiving data from server"

        return! clientLoop observer
        }

    { new IClient with 
        member x.Dispose() = tcpClient.Dispose()
        member x.Send(ssm) = 
            match ssm with
            | ByteMessage(b) -> 

                writeStream.WriteByte(0uy)
                let length = BitConverter.GetBytes(b.Count)
                writeStream.Write(length, 0, length.Length)
                writeStream.Write(b.Array, b.Offset, b.Count)
                writeStream.Flush()
            | StringMessage(s) -> 
                writeStream.WriteByte(1uy)
                let stringBytes = System.Text.Encoding.UTF8.GetBytes(s, 0, s.Length, toSendBuffer, 0)
                let lengthArray = BitConverter.GetBytes(stringBytes)
                writeStream.Write(lengthArray, 0, lengthArray.Length)
                writeStream.Write(toSendBuffer, 0, stringBytes)
                writeStream.Flush()
        member x.ReceivedMessages = 
            Observable.Create(fun (observer: IObserver<_>) -> 
                let cts = new CancellationTokenSource()

                let loopAsync = clientLoop observer
                Async.Start(loopAsync, cts.Token)

                { new IDisposable with member x.Dispose() = cts.Cancel(); tcpClient.Dispose() }
                )
                }

