namespace Ai.AgentFramwork.Massar.Web.Agents;

public interface IAgentModelSelector
{
    string GetModelForAgent(string agentName);

    void SetModelForAgent(string agentName, string modelKey);
}