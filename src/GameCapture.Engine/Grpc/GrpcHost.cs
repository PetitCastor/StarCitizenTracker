using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameCapture.Engine.Grpc;

/// <summary>
/// Builds the engine's gRPC host. Program and the integration tests share this one factory so
/// a test can never pass because it configured Kestrel differently from the real engine.
/// </summary>
internal static class GrpcHost
{
    /// <summary>
    /// A named pipe rather than a TCP port: the engine is a per-user process talking to
    /// plugins on the same machine, and a pipe inherits the session's ACL instead of exposing
    /// a listening socket. HTTP/2 is forced because gRPC requires it and pipes carry no TLS
    /// to negotiate it with.
    /// </summary>
    /// <remarks>
    /// Every engine component is registered as a singleton and the service itself is registered
    /// as an instance: gRPC's activator resolves a registered service from DI and only falls back
    /// to reflective construction (which cannot see the internal constructor) when it finds none.
    /// </remarks>
    public static WebApplication BuildGrpcHost(
        string pipeName,
        EngineStatus status,
        SubscriptionRegistry registry,
        ScanLoop scanLoop,
        OcrPipeline ocr,
        EngineConfig config)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders(); // the ConsoleSink owns the console, incl. the status bar
        builder.WebHost.ConfigureKestrel(k =>
            k.ListenNamedPipe(pipeName, o => o.Protocols = HttpProtocols.Http2));

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(status);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(scanLoop);
        builder.Services.AddSingleton(ocr);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(new CaptureGrpcService(status, registry, scanLoop, ocr, config));

        var app = builder.Build();
        app.MapGrpcService<CaptureGrpcService>();
        return app;
    }
}
