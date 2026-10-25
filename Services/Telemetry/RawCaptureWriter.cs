using System;
using System.IO;

namespace IotExpertApp.Services.Telemetry;

public sealed class RawCaptureWriter
{
    public const long DefaultLimit = 10 * 1024 * 1024;
    private readonly object gate = new object();
    private readonly long limit;
    private FileStream? file;
    private long written;
    private string description = "запись выключена";

    public RawCaptureWriter(long limit = DefaultLimit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        this.limit = limit;
    }

    public string Description { get { lock (gate) return description; } }

    public void Start(string path)
    {
        lock (gate)
        {
            Close();
            written = 0;
            if (string.IsNullOrWhiteSpace(path)) { description = "запись выключена"; return; }
            try
            {
                string absolute = Path.GetFullPath(path);
                // существующий файл не затираем, в поток не добавляем заголовок или повторный JSON
                file = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                description = "запись: " + absolute + "; 0 байт";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                description = "запись не начата: " + exception.Message;
            }
        }
    }

    // берем исходный блок целиком: при нехватке лимита закрываем файл перед блоком, прием продолжает идти
    // файловый сбой отражаем отдельно от приемника, синхронизация защищает Write от одновременного Stop
    public void Write(byte[] buffer, int count)
    {
        lock (gate)
        {
            if (file is null) return;
            try
            {
                if (count > limit - written)
                {
                    if (Close()) description = $"запись остановлена по лимиту {limit} байт; сохранено {written}; возможен оборванный хвост";
                    return;
                }
                file.Write(buffer, 0, count);
                written += count;
                description = $"запись активна: {file.Name}; {written} / {limit} байт";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Close();
                description = "ошибка записи: " + exception.Message;
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            bool active = file is not null;
            bool closed = Close();
            if (active && closed) description = $"запись завершена: {written} байт; последний кадр может быть неполным";
        }
    }

    private bool Close()
    {
        FileStream? previous = file;
        file = null;
        try { previous?.Dispose(); return true; }
        catch (IOException exception) { description = "ошибка закрытия записи: " + exception.Message; return false; }
    }
}
