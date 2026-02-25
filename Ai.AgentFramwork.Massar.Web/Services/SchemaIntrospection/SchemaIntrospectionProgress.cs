namespace Ai.AgentFramwork.Massar.Web.Services.SchemaIntrospection;

public sealed record SchemaIntrospectionProgress(
    string Phase,
    string Message,
    int? Current = null,
    int? Total = null);
