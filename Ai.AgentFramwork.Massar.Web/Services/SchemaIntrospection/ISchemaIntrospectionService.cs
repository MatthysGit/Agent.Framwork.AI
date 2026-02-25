using System.Threading;

namespace Ai.AgentFramwork.Massar.Web.Services.SchemaIntrospection;

public interface ISchemaIntrospectionService
{
    Task RunAsync(IProgress<SchemaIntrospectionProgress> progress, CancellationToken ct);
}
