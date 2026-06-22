using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vortice.Direct3D12.Debug;

namespace BallisticEngine.DX12;

public readonly struct DebugMessage {
    public MessageCategory Category { get; }
    public MessageSeverity Severity { get; }
    public MessageId Id { get; }
    public string Description { get; }
    public DebugMessage(MessageCategory category, MessageSeverity severity, MessageId id, string description) {
        Category = category; Severity = severity; Id = id; Description = description;
    }
    public bool IsErrorClass => Severity == MessageSeverity.Corruption || Severity == MessageSeverity.Error;
}
