using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class SecurityGroupMenuItem
{
    public int SecurityGroupId { get; set; }

    public int MenuItemId { get; set; }

    public bool IsEnabled { get; set; }

    public virtual SecurityMenuItem MenuItem { get; set; } = null!;
}
