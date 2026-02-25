using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class TableFieldDefinitionRoleAccess
{
    public long TableFieldDefinitionRoleAccessId { get; set; }

    public string TableName { get; set; } = null!;

    public string ColumnName { get; set; } = null!;

    public int RoleId { get; set; }

    public bool CanRead { get; set; }

    public bool CanWrite { get; set; }

    public string UserId { get; set; } = null!;

    public string? Comment { get; set; }

    public virtual Role Role { get; set; } = null!;

    public virtual TableFieldDefinition TableFieldDefinition { get; set; } = null!;

    public virtual AppUser User { get; set; } = null!;
}
