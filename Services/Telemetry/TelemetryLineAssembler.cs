using System.Collections.Generic;
using System.Text;
using IotExpertApp.Models.Telemetry;

namespace IotExpertApp.Services.Telemetry;

public sealed class TelemetryLineAssembler
{
    private readonly List<byte> bytes = new List<byte>(8191);
    private bool dropping;
    private bool jsonCandidate;

    // один read может содержать кусок строки или сразу несколько строк, поэтому хвост держим между вызовами
    // полный кадр ограничен 8192 байтами вместе с LF: до разделителя храним не больше 8191 байта
    // после переполнения отбрасываем байты до LF и начинаем следующую строку с чистого состояния
    public IReadOnlyList<Frame> Feed(byte[] buffer, int count)
    {
        List<Frame> result = new List<Frame>();
        for (int index = 0; index < count; index++)
        {
            byte value = buffer[index];
            if (value == 10)
            {
                if (dropping)
                    result.Add(new Frame(null, true, jsonCandidate));
                else
                {
                    string text = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                    result.Add(new Frame(text, false, text.StartsWith("{")));
                }
                Reset();
            }
            else if (!dropping)
            {
                if (bytes.Count == 0) jsonCandidate = value == (byte)'{';
                if (bytes.Count == 8191)
                {
                    dropping = true;
                    bytes.Clear();
                }
                else bytes.Add(value);
            }
        }
        return result;
    }

    // EOF и остановка не превращают незавершенный JSON в пакет, а только учитывают и очищают хвост
    public bool Finish()
    {
        bool tail = dropping || bytes.Count > 0;
        Reset();
        return tail;
    }

    private void Reset()
    {
        bytes.Clear();
        dropping = false;
        jsonCandidate = false;
    }
}
