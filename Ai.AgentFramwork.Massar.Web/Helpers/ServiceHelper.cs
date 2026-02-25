using System.Data;
using System.Data.Common;
using System.Reflection;

namespace Ai.AgentFramwork.Massar.ServiceDefaults.Helpers;

public static class ServiceHelper
{
    public static DataTable ToDataTable<T>(List<T> items)
    {
        var dataTable = new DataTable(typeof(T).Name);
        //Get all the properties
        var Props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in Props)
            //Setting column names as Property names
            dataTable.Columns.Add(prop.Name);
        foreach (var item in items)
        {
            var values = new object[Props.Length];
            for (var i = 0; i < Props.Length; i++)
                //inserting property values to datatable rows
                // Use DBNull.Value when the property value is null to avoid assigning null to a non-nullable object element
                values[i] = Props[i].GetValue(item, null) ?? DBNull.Value;
            dataTable.Rows.Add(values);
        }

        //put a breakpoint here and check datatable
        return dataTable;
    }


    public static List<T> MapToList<T>(this DbDataReader dr) where T : new()
    {
        var entities = new List<T>();

        if (dr == null || !dr.HasRows) return entities;

        var entityType = typeof(T);
        var props = entityType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var propDict = props.ToDictionary(p => p.Name.ToUpper(), p => p);

        while (dr.Read())
        {
            var newObject = new T();
            for (var index = 0; index < dr.FieldCount; index++)
            {
                var columnName = dr.GetName(index).ToUpper();
                if (propDict.TryGetValue(columnName, out var info))
                    if (info != null && info.CanWrite)
                    {
                        var val = dr.GetValue(index);
                        info.SetValue(newObject, val == DBNull.Value ? null : val, null);
                    }
            }

            entities.Add(newObject);
        }

        return entities;
    }
}