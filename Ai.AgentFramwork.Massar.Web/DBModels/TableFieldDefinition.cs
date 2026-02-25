using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class TableFieldDefinition
{
    public string table_name { get; set; } = null!;

    public string column_name { get; set; } = null!;

    public string? data_type { get; set; }

    public string? definition { get; set; }

    public virtual ICollection<TableFieldDefinitionRoleAccess> TableFieldDefinitionRoleAccesses { get; set; } = new List<TableFieldDefinitionRoleAccess>();

    public virtual TableDefinition table_nameNavigation { get; set; } = null!;
}
