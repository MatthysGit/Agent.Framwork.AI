using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class TableDefinition
{
    public string Name { get; set; } = null!;

    public string? Definition { get; set; }

    public virtual ICollection<TableFieldDefinition> TableFieldDefinitions { get; set; } = new List<TableFieldDefinition>();
}
