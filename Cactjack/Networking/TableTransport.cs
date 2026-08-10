using System;
using System.Text.Json;

namespace Cactjack.Networking;

public enum TableCommandType
{
    JoinSeat,
    KickSeat,
    ToggleSeatOpen,
    SetReady,
    SetWager,
    RenameSeat,
    DisconnectSeat,
    ReconnectSeat,
    LeaveSeat,
    StartRound,
    Hit,
    Stand,
    NextRound
}

public readonly record struct TableCommand(
    TableCommandType Type,
    int SeatIndex = -1,
    int Value = 0,
    string Text = "",
    string SenderId = "host",
    string RequestId = "",
    long ExpectedRevision = -1);

public interface ITableTransport : IDisposable
{
    event Action<TableCommand>? CommandReceived;
    event Action<TableSnapshot>? SnapshotReceived;
    void Send(TableCommand command);
    void Publish(TableSnapshot snapshot);
}

public sealed class LocalLoopbackTransport : ITableTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public event Action<TableCommand>? CommandReceived;
    public event Action<TableSnapshot>? SnapshotReceived;

    public void Send(TableCommand command)
    {
        var wire = JsonSerializer.Serialize(command, JsonOptions);
        CommandReceived?.Invoke(JsonSerializer.Deserialize<TableCommand>(wire, JsonOptions));
    }

    public void Publish(TableSnapshot snapshot)
    {
        var wire = JsonSerializer.Serialize(snapshot, JsonOptions);
        var copy = JsonSerializer.Deserialize<TableSnapshot>(wire, JsonOptions);
        if (copy is not null) SnapshotReceived?.Invoke(copy);
    }

    public void Dispose()
    {
        CommandReceived = null;
        SnapshotReceived = null;
    }
}
