using Microsoft.Data.SqlClient;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Third (and last) DB-touching class, same shape as Analyzer.cs/HealthCheckAnalyzer.cs: private
/// Open() via ConnectionFactory, one method per data-gathering step, no internal catching (that's
/// the runner's job). Enumerates every online user database and its principals/roles/explicit
/// permissions via 3-part-name catalog-view queries over a single connection - no per-database
/// connection switching needed.
/// </summary>
public sealed class PermissionsAnalyzer
{
    private readonly AnalyzerOptions _opt;

    public PermissionsAnalyzer(AnalyzerOptions opt) => _opt = opt;

    private SqlConnection Open() => ConnectionFactory.Open(_opt);

    public List<string> ListDatabases()
    {
        var names = new List<string>();
        using var conn = Open();
        using var cmd = new SqlCommand(SqlPermissions.ListDatabases, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    public List<ServerPrincipalInfo> GetServerPrincipals()
    {
        var byName = new Dictionary<string, ServerPrincipalInfo>(StringComparer.OrdinalIgnoreCase);

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlPermissions.ServerPrincipals, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                byName[name] = new ServerPrincipalInfo
                {
                    Name = name,
                    TypeDesc = r.GetString(1),
                    IsDisabled = r.GetBoolean(2),
                    CreateDate = r.GetDateTime(3),
                    DefaultDatabaseName = r.IsDBNull(4) ? null : r.GetString(4)
                };
            }
        }

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlPermissions.ServerRoleMembers, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var login = r.GetString(0);
                var role = r.GetString(1);
                if (byName.TryGetValue(login, out var p)) p.ServerRoles.Add(role);
            }
        }

        return byName.Values.ToList();
    }

    /// <summary>sid (hex) -> login name, used to resolve which login owns each database user and to
    /// detect orphaned ones.</summary>
    public Dictionary<string, string> GetServerLoginsBySid()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd = new SqlCommand(SqlPermissions.ServerLoginSids, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var sidBytes = (byte[])r.GetValue(0);
            map[Convert.ToHexString(sidBytes)] = r.GetString(1);
        }
        return map;
    }

    public List<DatabaseUserInfo> GetDatabaseUsers(string database, Dictionary<string, string> loginsBySid)
    {
        var users = new List<DatabaseUserInfo>();
        var sql = string.Format(SqlPermissions.DatabaseUsersFmt, Analyzer.Escape(database));

        using (var conn = Open())
        using (var cmd = new SqlCommand(sql, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var sidBytes = r.IsDBNull(2) ? null : (byte[])r.GetValue(2);
                var sidHex = sidBytes is { Length: > 0 } ? Convert.ToHexString(sidBytes) : null;
                var authType = r.GetString(3);
                var loginName = sidHex != null && loginsBySid.TryGetValue(sidHex, out var name) ? name : null;

                users.Add(new DatabaseUserInfo
                {
                    DatabaseName = database,
                    UserName = r.GetString(0),
                    TypeDesc = r.GetString(1),
                    LoginName = loginName,
                    IsOrphaned = authType == "INSTANCE" && loginName == null
                });
            }
        }

        var roleMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var conn = Open())
        using (var cmd = new SqlCommand(string.Format(SqlPermissions.DatabaseRoleMembersFmt, Analyzer.Escape(database)), conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var user = r.GetString(0);
                if (!roleMap.TryGetValue(user, out var list)) roleMap[user] = list = [];
                list.Add(r.GetString(1));
            }
        }

        foreach (var u in users)
            if (roleMap.TryGetValue(u.UserName, out var roles))
                u.DatabaseRoles.AddRange(roles);

        return users;
    }

    public List<ObjectPermissionInfo> GetObjectPermissions(string database)
    {
        var objectNames = new Dictionary<int, (string Schema, string Name)>();
        using (var conn = Open())
        using (var cmd = new SqlCommand(string.Format(SqlPermissions.ObjectNamesFmt, Analyzer.Escape(database)), conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                objectNames[r.GetInt32(0)] = (r.GetString(1), r.GetString(2));
        }

        var schemaNames = new Dictionary<int, string>();
        using (var conn = Open())
        using (var cmd = new SqlCommand(string.Format(SqlPermissions.SchemaNamesFmt, Analyzer.Escape(database)), conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                schemaNames[r.GetInt32(0)] = r.GetString(1);
        }

        var perms = new List<ObjectPermissionInfo>();
        using (var conn = Open())
        using (var cmd = new SqlCommand(string.Format(SqlPermissions.DatabasePermissionsFmt, Analyzer.Escape(database)), conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var classDesc = r.GetString(2);
                var majorId = r.GetInt32(3);
                string? schemaName = null, objectName = null;

                if (classDesc == "OBJECT_OR_COLUMN" && objectNames.TryGetValue(majorId, out var obj))
                {
                    schemaName = obj.Schema;
                    objectName = obj.Name;
                }
                else if (classDesc == "SCHEMA" && schemaNames.TryGetValue(majorId, out var sch))
                {
                    schemaName = sch;
                }

                perms.Add(new ObjectPermissionInfo
                {
                    DatabaseName = database,
                    GranteeName = r.GetString(0),
                    GranteeType = r.GetString(1),
                    ClassDesc = classDesc,
                    PermissionName = r.GetString(4),
                    StateDesc = r.GetString(5),
                    SchemaName = schemaName,
                    ObjectName = objectName
                });
            }
        }

        return perms;
    }
}
