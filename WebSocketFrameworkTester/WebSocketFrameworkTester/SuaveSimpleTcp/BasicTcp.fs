﻿module Suave.BasicTcp

open System
open System.Collections.Generic
open System.Threading
open System.Net
open System.Net.Sockets
open Suave.Logging
open Suave.Logging.Message
open Suave.Sockets
open Suave.Utils

let private logger = Log.create "Suave.Tcp"

/// The max backlog of number of requests
[<Literal>]
let MaxBacklog = Int32.MaxValue

/// Stop the TCP listener server
let stopTcp reason (socket : Socket) =
  try
    logger.debug (
      eventX "Stopping TCP server {because}"
      >> setFieldValue "because" reason)

    socket.Dispose()

    logger.debug (eventX "Stopped TCP server")
  with ex ->
    logger.debug (eventX "Failure stopping TCP server" >> addExn ex)

open Suave.Sockets

let createTransport transportPool listenSocket =
  new Suave.Sockets.BasicTcpTransport(transportPool, listenSocket)

let createPools listenSocket logger maxOps bufferSize autoGrow =

  let transportPool = new ConcurrentPool<BasicTcpTransport>()
  transportPool.ObjectGenerator <- (fun _ -> createTransport (Some(transportPool)) listenSocket)

  let bufferManager = new BufferManager(bufferSize * (maxOps + 1), bufferSize, autoGrow)
  bufferManager.Init()

  //Pre-allocate a set of reusable transportObjects
  for x = 0 to maxOps - 1 do
    let transport = createTransport (Some(transportPool)) listenSocket
    transportPool.Push transport

  (transportPool, bufferManager)

// NOTE: performance tip, on mono set nursery-size with a value larger than MAX_CONCURRENT_OPS * BUFFER_SIZE
// i.e: export MONO_GC_PARAMS=nursery-size=128m
// The nursery size must be a power of two in bytes

let private aFewTimes f =
  let s ms = System.Threading.Thread.Sleep (ms : int)
  let rec run = function
    | 0us | 1us -> f ()
    | n -> try f () with e -> s 10; run (n - 1us)
  run 3us

let numberOfClients = ref 0
// consider:
// echo 5 > /proc/sys/net/ipv4/tcp_fin_timeout
// echo 1 > /proc/sys/net/ipv4/tcp_tw_recycle
// custom kernel with shorter TCP_TIMEWAIT_LEN in include/net/tcp.h
let job (serveClient : TcpWorker<unit>)
        binding
        (transport : ITransport)
        (bufferManager : BufferManager) = async {

  Interlocked.Increment numberOfClients |> ignore

  logger.debug (
    eventX "{client} connected. Now has {totalClients} connected"
    >> setFieldValue "client" (binding.ip.ToString())
    >> setFieldValue "totalClients" (!numberOfClients))

  let connection =
    { socketBinding   = binding
      transport       = transport
      bufferManager   = bufferManager
      lineBuffer      = bufferManager.PopBuffer "Suave.Tcp.job"
      segments        = []
      lineBufferCount = 0 }

  try
    use! oo = Async.OnCancel (fun () ->
      logger.debug (eventX "Disconnected client (async cancel)")
      Async.RunSynchronously (transport.shutdown()))

    do! serveClient connection
  with
    | :? System.IO.EndOfStreamException ->
      logger.debug (eventX "Disconnected client (end of stream)")

    | ex ->
      logger.warn (eventX "TCP request processing failed" >> addExn ex)

  bufferManager.FreeBuffer(connection.lineBuffer, "Suave.Tcp.job")
  logger.debug (eventX "Shutting down transport")
  do! transport.shutdown()
  Interlocked.Decrement(numberOfClients) |> ignore
  logger.debug (
    eventX "Disconnected {client}. {totalClients} connected."
    >> setFieldValue "client" binding.ip
    >> setFieldValue "totalClients" (!numberOfClients))
  }

type TcpServer = Tcp.StartedData -> AsyncResultCell<Tcp.StartedData> -> TcpWorker<unit> -> Async<unit>

let runServer maxConcurrentOps bufferSize autoGrow (binding: SocketBinding) startData
              (acceptingConnections: AsyncResultCell<Tcp.StartedData>) serveClient = async {
  try
    use listenSocket = new Socket(binding.endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
    listenSocket.NoDelay <- true

    let transportPool, bufferManager =
      createPools listenSocket logger maxConcurrentOps bufferSize autoGrow

    aFewTimes (fun () -> listenSocket.Bind binding.endpoint)
    listenSocket.Listen MaxBacklog

    use! disposable = Async.OnCancel(fun () ->
      stopTcp "runServer async cancelled" listenSocket)

    let startData =
      { startData with Tcp.socketBoundUtc = Some (Globals.utcNow()) }

    acceptingConnections.complete startData |> ignore

    logger.info (
      eventX "Smooth! Suave listener started in {startedListeningMilliseconds:#.###} with binding {ipAddress}:{port}"
      >> setFieldValue "startedListeningMilliseconds" (startData.GetStartedListeningElapsedMilliseconds())
      // .Address can throw exceptions, just log its string representation
      >> setFieldValue "ipAddress" (startData.binding.ip.ToString())
      >> setFieldValue "port" startData.binding.port
      >> setSingleName "Suave.Tcp.runServer")

    let! token = Async.CancellationToken

    while not (token.IsCancellationRequested) do
      try
        let transport = transportPool.Pop()
        let! r = transport.accept()
        match r with
        | Choice1Of2 remoteBinding ->
          // start a new async worker for each accepted TCP client
          Async.Start (job serveClient remoteBinding transport bufferManager, token)
        | Choice2Of2 e ->
          failwithf "Socket failed to accept client, error: %A" e

      with ex ->
        logger.error (eventX "Socket failed to accept a client" >> addExn ex)

  with ex ->
    logger.fatal (eventX "TCP server failed" >> addExn ex)
    return raise ex
}

/// Start a new TCP server with a specific IP, Port and with a serve_client worker
/// returning an async workflow whose result can be awaited (for when the tcp server has started
/// listening to its address/port combination), and an asynchronous workflow that
/// yields when the full server is cancelled. If the 'has started listening' workflow
/// returns None, then the start timeout expired.
let startTcpIpServerAsync (serveClient : TcpWorker<unit>)
                          (binding     : SocketBinding)
                          (runServer   : TcpServer) =

  let acceptingConnections = new AsyncResultCell<Tcp.StartedData>()

  let startData =
        { Tcp.startCalledUtc = Globals.utcNow ()
          Tcp.socketBoundUtc = None
          Tcp.binding        = binding }

  acceptingConnections.awaitResult()
    , runServer startData acceptingConnections serveClient

