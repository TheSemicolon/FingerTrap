using System.Runtime.InteropServices;
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

await using var pty = CreatePtyService();
using var surface = new RpcSurface(pty);

var rpc = new JsonRpc(handler);
rpc.AddLocalRpcTarget(surface, new JsonRpcTargetOptions
{
    MethodNameTransform = CommonMethodNameTransforms.CamelCase,
});
surface.AttachRpc(rpc);
rpc.StartListening();

Console.Error.WriteLine("fingertrap-sidecar: listening on stdio");
await rpc.Completion;
Console.Error.WriteLine("fingertrap-sidecar: rpc completion");

static IPtyService CreatePtyService()
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
    {
        return new LinuxPtyService();
    }

    return new UnsupportedPtyService();
}
