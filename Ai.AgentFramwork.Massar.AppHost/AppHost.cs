var builder = DistributedApplication.CreateBuilder(args);

var openai = builder.AddConnectionString("openai");


var markitdown = builder.AddContainer("markitdown", "mcp/markitdown")
    .WithArgs("--http", "--host", "0.0.0.0", "--port", "3001")
    .WithHttpEndpoint(targetPort: 3001, name: "http").WithEndpoint("http", e =>
    {
        e.Port = 5002;
        e.TargetPort = 3001;
    }); ;

var webApp = builder.AddProject<Projects.Ai_AgentFramwork_Massar_Web>("aichatweb-app")
    .WithEndpoint("http", e => e.Port = 5000)
    .WithEndpoint("https", e => e.Port = 5001);

webApp.WithReference(openai);

//webApp
//    .WithReference(vectorDB)
//    .WaitFor(vectorDB);

webApp
    .WithEnvironment("MARKITDOWN_MCP_URL", markitdown.GetEndpoint("http"));

builder.Build().Run();
