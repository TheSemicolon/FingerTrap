using FingerTrap.Sidecar.Abstractions;
using FingerTrap.Sidecar.Ipc;
using FingerTrap.Sidecar.Pty;
using Nerdbank.Streams;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;

// stdout is owned by the JSON-RPC framing — any Console.Write here corrupts
// the stream. All status output goes to stderr (ADR-0002).
Console.Error.WriteLine("fingertrap-sidecar: starting");

var stdio = FullDuplexStream.Splice(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput());

var formatter = new JsonMessageFormatter();
formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();

var handler = new HeaderDelimitedMessageHandler(stdio, stdio, formatter);

// Single platform-agnostic PtyService backed by Porta.Pty (ADR-0008);
// platform branching now lives inside the vendored library.
await using var pty = new PtyService();
using var surface = new RpcSurface(pty);

var rpc = new JsonRpc(handler);
rpc.AddLocalRpcTarget(surface, new JsonRpcTargetOptions
{
    MethodNameTransform = CommonMethodNameTransforms.CamelCase,
    // vscode-jsonrpc's RequestType1<T> with the default ParameterStructures.auto
    // serializes a single object arg as `params: {...}` (named). With this
    // flag, StreamJsonRpc deserializes the entire params object into the
    // method's single non-CancellationToken parameter, instead of trying to
    // match each top-level key as a separate named argument.
    UseSingleObjectParameterDeserialization = true,
});
surface.AttachRpc(rpc);
rpc.StartListening();

Console.Error.WriteLine("fingertrap-sidecar: listening on stdio");
await rpc.Completion;
Console.Error.WriteLine("fingertrap-sidecar: rpc completion");
