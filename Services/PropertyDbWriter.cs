using Microsoft.Data.Sqlite;

namespace ApsMdExtractor.Services;

/// <summary>One object (dbId) and its properties, as returned by Model Derivative.</summary>
public class DbObject
{
    public long Id;
    public string? ExternalId;
    public string? Name;
    public long? ParentId;
    public List<(string Category, string Name, object? Value)> Props = [];
}

/// <summary>
/// Writes a SQLite file that uses the same tables as the Viewer property database (model.sdb):
/// _objects_id, _objects_attr, _objects_val and _objects_eav.
/// A _metadata table records how the file was produced.
/// </summary>
public static class PropertyDbWriter
{
    // Attribute data types used by the property database.
    private const int TypeBoolean = 1, TypeInteger = 2, TypeDouble = 3, TypeDbKey = 11, TypeString = 20;
    // Flag 1 hides an attribute from the Viewer property panel.
    private const int FlagHidden = 1;

    public static void Write(string path, IEnumerable<DbObject> objects, IReadOnlyDictionary<string, string> metadata)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, tx, """
            CREATE TABLE _objects_id (id INTEGER PRIMARY KEY, external_id BLOB, viewable_id BLOB);
            CREATE TABLE _objects_attr (id INTEGER PRIMARY KEY, name TEXT, category TEXT, data_type INTEGER, data_type_context TEXT, description TEXT, display_name TEXT, flags INTEGER, display_precision INTEGER, forge_parameter TEXT);
            CREATE TABLE _objects_val (id INTEGER PRIMARY KEY, value BLOB);
            CREATE TABLE _objects_eav (id INTEGER PRIMARY KEY, entity_id INTEGER, attribute_id INTEGER, value_id INTEGER);
            CREATE TABLE _metadata (key TEXT PRIMARY KEY, value TEXT);
            CREATE INDEX idx_eav_entity ON _objects_eav (entity_id);
            """);

        var attrIds = new Dictionary<(string, string, int), long>();
        var valIds = new Dictionary<(Type, object), long>();

        using var insId = Cmd(conn, tx, "INSERT INTO _objects_id (id, external_id, viewable_id) VALUES ($id, $ext, NULL)", "$id", "$ext");
        using var insAttr = Cmd(conn, tx, "INSERT INTO _objects_attr (id, name, category, data_type, data_type_context, description, display_name, flags, display_precision, forge_parameter) VALUES ($id, $name, $cat, $type, NULL, NULL, $name, $flags, 0, NULL)", "$id", "$name", "$cat", "$type", "$flags");
        using var insVal = Cmd(conn, tx, "INSERT INTO _objects_val (id, value) VALUES ($id, $value)", "$id", "$value");
        using var insEav = Cmd(conn, tx, "INSERT INTO _objects_eav (entity_id, attribute_id, value_id) VALUES ($e, $a, $v)", "$e", "$a", "$v");

        long AttrId(string category, string name, int type, int flags)
        {
            var key = (category, name, type);
            if (!attrIds.TryGetValue(key, out var id))
            {
                id = attrIds.Count + 1;
                attrIds[key] = id;
                Run(insAttr, id, name, category, type, flags);
            }
            return id;
        }

        long ValId(object value)
        {
            var key = (value.GetType(), value);
            if (!valIds.TryGetValue(key, out var id))
            {
                id = valIds.Count + 1;
                valIds[key] = id;
                Run(insVal, id, value);
            }
            return id;
        }

        void Eav(long entity, string category, string name, object? value, int? forcedType = null, int flags = 0)
        {
            if (value is null)
            {
                return;
            }
            var (type, stored) = forcedType is int t ? (t, value) : Classify(value);
            Run(insEav, entity, AttrId(category, name, type, flags), ValId(stored));
        }

        var list = objects.OrderBy(o => o.Id).ToList();
        var children = list.Where(o => o.ParentId.HasValue)
            .GroupBy(o => o.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        foreach (var obj in list)
        {
            Run(insId, obj.Id, obj.ExternalId ?? "");
            Eav(obj.Id, "__name__", "name", obj.Name, TypeString, FlagHidden);
            if (obj.ParentId is long parent)
            {
                Eav(obj.Id, "__parent__", "parent", parent, TypeDbKey, FlagHidden);
            }
            if (children.TryGetValue(obj.Id, out var kids))
            {
                foreach (var kid in kids)
                {
                    Eav(obj.Id, "__child__", "child", kid, TypeDbKey, FlagHidden);
                }
            }
            foreach (var (category, name, value) in obj.Props)
            {
                Eav(obj.Id, category, name, value);
            }
        }

        using var insMeta = Cmd(conn, tx, "INSERT INTO _metadata (key, value) VALUES ($k, $v)", "$k", "$v");
        foreach (var (key, value) in metadata)
        {
            Run(insMeta, key, value);
        }
        tx.Commit();
    }

    private static (int type, object value) Classify(object value) => value switch
    {
        bool b => (TypeBoolean, b ? 1L : 0L),
        int or long => (TypeInteger, Convert.ToInt64(value)),
        float or double or decimal => (TypeDouble, Convert.ToDouble(value)),
        _ => (TypeString, value.ToString() ?? "")
    };

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Cmd(SqliteConnection conn, SqliteTransaction tx, string sql, params string[] names)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var name in names)
        {
            cmd.Parameters.Add(new SqliteParameter { ParameterName = name });
        }
        return cmd;
    }

    private static void Run(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
        }
        cmd.ExecuteNonQuery();
    }
}
