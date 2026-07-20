namespace ColumnstoreAnalyzer;

/// <summary>
/// T-SQL for the standalone --permissions-report mode. Enumerates every online user database
/// on the instance via 3-part names against per-database CATALOG VIEWS (sys.database_principals,
/// sys.database_role_members, sys.database_permissions, sys.objects, sys.schemas) - these resolve
/// correctly cross-database when qualified with a database prefix (unlike most sys.dm_* dynamic
/// management views), so this needs only one connection and one dynamic query per database, no
/// per-database connection switching.
/// </summary>
internal static class SqlPermissions
{
    public const string ListDatabases = @"
SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name;";

    public const string ServerPrincipals = @"
SELECT name, type_desc, is_disabled, create_date, default_database_name
FROM sys.server_principals
WHERE type IN ('S','U','G')
ORDER BY name;";

    /// <summary>sid + name only, for cross-referencing database users back to their owning login.</summary>
    public const string ServerLoginSids = @"
SELECT sid, name FROM sys.server_principals WHERE type IN ('S','U','G') AND sid IS NOT NULL;";

    public const string ServerRoleMembers = @"
SELECT sp.name AS login_name, r.name AS server_role
FROM sys.server_role_members rm
JOIN sys.server_principals sp ON sp.principal_id = rm.member_principal_id
JOIN sys.server_principals r ON r.principal_id = rm.role_principal_id
ORDER BY sp.name;";

    /// <summary>Database users (param: {0} = escaped database name). authentication_type_desc = 'INSTANCE'
    /// means the user should map to a server login by sid; 'DATABASE'/'NONE' users (contained DBs,
    /// certificate/key mapped) are by design login-less and must not be flagged as orphaned.</summary>
    public const string DatabaseUsersFmt = @"
SELECT name, type_desc, sid, authentication_type_desc
FROM {0}.sys.database_principals
WHERE type IN ('S','U','G') AND principal_id > 4
ORDER BY name;";

    public const string DatabaseRoleMembersFmt = @"
SELECT m.name AS user_name, r.name AS role_name
FROM {0}.sys.database_role_members rm
JOIN {0}.sys.database_principals m ON m.principal_id = rm.member_principal_id
JOIN {0}.sys.database_principals r ON r.principal_id = rm.role_principal_id
ORDER BY m.name;";

    /// <summary>Explicit grants only - fixed database roles' built-in permissions are implicit and never
    /// appear as rows here, so this is naturally just the custom/audit-worthy grants, no extra filtering
    /// needed. class IN (0,1,3) = DATABASE, OBJECT_OR_COLUMN, SCHEMA - the practically useful scopes.</summary>
    public const string DatabasePermissionsFmt = @"
SELECT
    dp.name AS grantee_name,
    dp.type_desc AS grantee_type,
    perm.class_desc,
    perm.major_id,
    perm.permission_name,
    perm.state_desc
FROM {0}.sys.database_permissions perm
JOIN {0}.sys.database_principals dp ON dp.principal_id = perm.grantee_principal_id
WHERE perm.class IN (0, 1, 3)
ORDER BY dp.name;";

    /// <summary>object_id -> (schema, name) lookup, resolved in C# rather than a conditional SQL join.</summary>
    public const string ObjectNamesFmt = @"
SELECT o.object_id, s.name AS schema_name, o.name AS object_name
FROM {0}.sys.objects o
JOIN {0}.sys.schemas s ON s.schema_id = o.schema_id;";

    public const string SchemaNamesFmt = @"
SELECT schema_id, name FROM {0}.sys.schemas;";
}
