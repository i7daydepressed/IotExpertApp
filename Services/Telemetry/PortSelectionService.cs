using System;
using System.Collections.Generic;
using System.Linq;

namespace IotExpertApp.Services.Telemetry;

public static class PortSelectionService {
    // это только предложение в списке: принадлежность порта ESP32 проверяет принятая телеметрия
    public static string? Choose(string? current, IEnumerable<string> ports) {
        string[] available = ports.Distinct(StringComparer.Ordinal).ToArray();
        if (current is not null && available.Contains(current, StringComparer.Ordinal)) return current;
        string[] candidates = available.Where(port => port.StartsWith("/dev/cu.usbserial", StringComparison.Ordinal)
            || port.StartsWith("/dev/cu.SLAB_USBtoUART", StringComparison.Ordinal)
            || port.StartsWith("/dev/cu.usbmodem", StringComparison.Ordinal)
            || port.StartsWith("/dev/ttyUSB", StringComparison.Ordinal)
            || port.StartsWith("/dev/ttyACM", StringComparison.Ordinal)
            || (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && port.Length > 3 && port[3..].All(char.IsAsciiDigit))).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
