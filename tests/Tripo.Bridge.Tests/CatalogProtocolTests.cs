using System.IO.Pipes;
using System.Text.Json;
using Xunit;

namespace Tripo.Bridge.Tests;

public sealed class CatalogProtocolTests
{
    [Fact]
    public async Task PreviousV3BridgeCannotDispatchAnImportToCurrentV4Host()
    {
        using TemporaryDataRoot dataRoot = new();
        RejectUnexpectedDispatch dispatcher = new();
        await using NamedPipeBridgeServer server = new("revit", "test", [BridgeConstants.ImportMeshMethod], dispatcher);
        await server.StartAsync();
        await using NamedPipeClientStream pipe = new(".", server.Descriptor.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync();
        BridgeRequest request = new("3", Guid.NewGuid().ToString("D"), server.Descriptor.SessionToken,
            BridgeConstants.ImportMeshMethod, BridgeJson.ToElement(new { name = "Old client import" }));
        await BoundedJsonLine.WriteAsync(pipe, request, BridgeConstants.MaximumMessageBytes, CancellationToken.None);
        BridgeResponse response = await BoundedJsonLine.ReadAsync<BridgeResponse>(pipe, BridgeConstants.MaximumMessageBytes, CancellationToken.None);
        Assert.Equal("4", BridgeConstants.ProtocolVersion);
        Assert.False(response.Ok);
        Assert.Equal("unsupported_protocol", response.Error?.Code);
        Assert.Equal(0, dispatcher.CallCount);
    }

    [Fact]
    public async Task CurrentClientCannotDiscoverPreviousV3Bridge()
    {
        using TemporaryDataRoot dataRoot = new();
        await using NamedPipeBridgeServer server = new("revit", "test", [BridgeConstants.ImportMeshMethod], new RejectUnexpectedDispatch());
        await server.StartAsync();
        string path = BridgePaths.GetSessionDescriptorPath("revit", Environment.ProcessId);
        File.WriteAllText(path, JsonSerializer.Serialize(server.Descriptor with { ProtocolVersion = "3" }, BridgeJson.Options));
        Assert.Equal("host_unavailable", Assert.Throws<BridgeCallException>(() => BridgeDiscovery.FindSession("revit", Environment.ProcessId)).Code);
    }

    private sealed class RejectUnexpectedDispatch : IHostBridgeDispatcher
    {
        public int CallCount { get; private set; }

        public Task<object> DispatchAsync(string method, JsonElement payload, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("An incompatible client must not reach host mutation dispatch.");
        }
    }
}
