namespace Suave.Sockets

open System
open System.Net
open System.Net.Sockets
open System.IO

type BasicTcpTransport(transportPool: ConcurrentPool<BasicTcpTransport> option, listenSocket : Socket) =

  let remoteBinding (socket : Socket) =
    let rep = socket.RemoteEndPoint :?> IPEndPoint
    { ip = rep.Address; port = uint16 rep.Port }

  let mutable remoteSocketStream : (Stream * Stream * Socket) option = None

  let writeSemaphore = new System.Threading.SemaphoreSlim(1, 1)

  member this.accept() =
      async {
          try
            let! acceptedSocket = Async.FromBeginEnd(listenSocket.BeginAccept, listenSocket.EndAccept)
            let readStream = new BufferedStream(new NetworkStream(acceptedSocket, false), 100000) :> Stream
            let writeStream = new BufferedStream(new NetworkStream(acceptedSocket, false), 100000) :> Stream
            remoteSocketStream <- Some (readStream, writeStream, acceptedSocket)
            //printfn "Client accepted"
            return Choice1Of2 (remoteBinding acceptedSocket)
          with
          | ex -> return Choice2Of2 (ConnectionError "read error: acceptArgs.AcceptSocket = null") 
      }

  interface ITransport with
    member this.read (buf : ByteSegment) =
      async{
       let! cancelToken = Async.CancellationToken
       return!
        match remoteSocketStream with
        | None -> async { return Choice2Of2 (ConnectionError "read error: acceptArgs.AcceptSocket = null") }
        | Some(s, _, _) -> async {
            let! readResult = s.ReadAsync(buf.Array, buf.Offset, buf.Count, cancelToken) |> Async.AwaitTask
            return Choice1Of2 readResult
            }
       }

    member this.write (buf : ByteSegment) =
      async{
       let! cancelToken = Async.CancellationToken
       return!
        match remoteSocketStream with
        | None -> async { return Choice2Of2 (ConnectionError "read error: acceptArgs.AcceptSocket = null") }
        | Some(_, s, _) -> async {
            //printfn "Attempting to write"
            do! writeSemaphore.WaitAsync() |> Async.AwaitTask
            do! s.WriteAsync(buf.Array, buf.Offset, buf.Count, cancelToken) |> Async.AwaitTask
            writeSemaphore.Release() |> ignore
            //printfn "Write success"
            //do! s.FlushAsync(cancelToken) |> Async.AwaitTask
            return Choice1Of2 ()
            }
       }

    member this.shutdown() = async {
      remoteSocketStream |> Option.iter (fun (read, write, socket) -> read.Dispose(); write.Dispose(); socket.Dispose() )
      remoteSocketStream <- None
      transportPool |> Option.iter (fun x -> x.Push(this))
      //printfn "Client shutdown"
      return ()
      }