using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class Agent
{
    public string AgentName { get; set; } = null!;

    public string AgentModel { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime CreatedOn { get; set; }
}
