using CaptureContracts.Proto;
using Grpc.Core;

namespace CaptureEngine.Grpc;

/// <summary>
/// The engine's whole public surface. It holds no state of its own: everything it reports
/// comes from <see cref="EngineStatus"/>, so the scan loop and the RPC layer can never
/// disagree about what the engine is doing.
/// </summary>
public sealed class CaptureGrpcService : CaptureEngineService.CaptureEngineServiceBase
{
    private readonly EngineStatus _status;

    public CaptureGrpcService(EngineStatus status) => _status = status;

    // TASK-2: only GetStatus. Track/ReadRoi/DumpFrame arrive in TASK-3 and until then keep
    // the base implementation (gRPC returns UNIMPLEMENTED).
    public override Task<StatusResponse> GetStatus(StatusRequest request, ServerCallContext ctx)
        => Task.FromResult(_status.Snapshot());
}
