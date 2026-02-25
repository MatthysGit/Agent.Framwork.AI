using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class SecurityMenuItem
{
    public int MenuItemId { get; set; }

    public int? ParentMenuItemId { get; set; }

    public byte ItemType { get; set; }

    public string Title { get; set; } = null!;

    public string? Href { get; set; }

    public string? IconKey { get; set; }

    public string? IconSize { get; set; }

    public string? IconColor { get; set; }

    public string? CssClass { get; set; }

    public string? Style { get; set; }

    public bool MatchAll { get; set; }

    public int SortOrder { get; set; }

    public bool IsEnabled { get; set; }

    public virtual ICollection<SecurityMenuItem> InverseParentMenuItem { get; set; } = new List<SecurityMenuItem>();

    public virtual SecurityMenuItem? ParentMenuItem { get; set; }

    public virtual ICollection<SecurityGroupMenuItem> SecurityGroupMenuItems { get; set; } = new List<SecurityGroupMenuItem>();
}
