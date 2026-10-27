using System;
using System.IO;
using System.IO.Ports;

namespace IotExpertApp.Services.Telemetry;

public interface ITelemetryByteSource : IDisposable {
    int Read(byte[] buffer);
}

public sealed class SerialTelemetrySource : ITelemetryByteSource {
    private readonly SerialPort port;

    public SerialTelemetrySource(string name) {
        port = new SerialPort(name, 115200, Parity.None, 8, StopBits.One) {
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 250
        };
        try { port.Open(); }
        catch { port.Dispose(); throw; }
    }

    public int Read(byte[] buffer) => port.Read(buffer, 0, buffer.Length);
    public void Dispose() => port.Dispose();
}

public sealed class ReplayTelemetrySource : ITelemetryByteSource {
    private readonly FileStream file;
    public ReplayTelemetrySource(string path) => file = File.OpenRead(path);
    public int Read(byte[] buffer) => file.Read(buffer, 0, buffer.Length);
    public void Dispose() => file.Dispose();
}
