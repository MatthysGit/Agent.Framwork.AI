using System;
using System.Collections.Generic;

namespace Ai.AgentFramwork.Massar.Web.DBModels;

public partial class TableReferencing
{
    public int id { get; set; }

    public string table_name { get; set; } = null!;

    public string referencing_table_name { get; set; } = null!;

    public string? referencing_column_name { get; set; }
}
