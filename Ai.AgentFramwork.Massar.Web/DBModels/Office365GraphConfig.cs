using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class Office365GraphConfig
{
    public int Id { get; set; }

    public string TenantId { get; set; } = null!;

    public string ClientId { get; set; } = null!;

    public string ClientSecret { get; set; } = null!;

    public string FromUser { get; set; } = null!;

    public bool IsActive { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
