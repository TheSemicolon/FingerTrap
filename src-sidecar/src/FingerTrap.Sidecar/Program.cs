using FingerTrap.Sidecar.Ipc;
using Nerdbank.Streams;
using StreamJsonRpc;

// stdout is owned by the JSON-RPC framing — any Console.Write here corrupts
// the stream. All status output goes to stderr (ADR-0002).
Console.Error.WriteLine("fingertrap-sidecar: starting");

var stdio = FullDuplexStream.Splice(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput());

var handler = new HeaderDelimitedMessageHandler(stdio, stdio, new JsonMessageFormatter());
var rpc = new JsonRpc(handler);
rpc.AddLocalRpcTarget(new RpcSurface());
rpc.StartListening();

Console.Error.WriteLine("fingertrap-sidecar: listening on stdio");
await rpc.Completion;
Console.Error.WriteLine("fingertrap-sidecar: rpc completion");
