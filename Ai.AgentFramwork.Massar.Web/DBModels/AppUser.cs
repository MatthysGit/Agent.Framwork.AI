using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class AppUser
{
    public string UserId { get; set; } = null!;

    public string? UserName { get; set; }

    public string? Email { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public string? PasswordHash { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public int? RoleId { get; set; }

    public virtual ICollection<AiTokenUsageLog> AiTokenUsageLogs { get; set; } = new List<AiTokenUsageLog>();

    public virtual AiUserMonthlyCostLimit? AiUserMonthlyCostLimit { get; set; }

    public virtual ICollection<ChatConversation> ChatConversations { get; set; } = new List<ChatConversation>();

    public virtual Role? Role { get; set; }

    public virtual ICollection<TableFieldDefinitionRoleAccess> TableFieldDefinitionRoleAccesses { get; set; } = new List<TableFieldDefinitionRoleAccess>();

    public virtual ICollection<UserSecurityGroup> UserSecurityGroups { get; set; } = new List<UserSecurityGroup>();

    public virtual ICollection<UserTeamRole> UserTeamRoles { get; set; } = new List<UserTeamRole>();
}
