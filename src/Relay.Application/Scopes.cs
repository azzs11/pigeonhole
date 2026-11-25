namespace Relay.Application;

public static class Scopes
{
    public const string MessagesSend = "messages:send";
    public const string MessagesRead = "messages:read";

    /// <summary>Create agent runs and decide approvals. A human or app privilege, never an agent's.</summary>
    public const string AgentsManage = "agents:manage";

    /// <summary>Only on run tokens: send through the Guard within one run.</summary>
    public const string AgentSend = "agent:send";

    /// <summary>Scopes a regular API key may hold. <see cref="AgentSend"/> is reserved for run tokens.</summary>
    public static IReadOnlyList<string> All { get; } = [MessagesSend, MessagesRead, AgentsManage];
}
