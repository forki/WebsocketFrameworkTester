namespace Suave

open Suave.Logging
open Suave.Sockets
open Suave.Tcp

type BasicTcpServerFactory() =
  interface TcpServerFactory with
    member this.create (maxOps, bufferSize, autoGrow, binding) =
      BasicTcp.runServer maxOps bufferSize autoGrow binding