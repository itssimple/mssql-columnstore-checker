namespace ColumnstoreAnalyzer.Tests;

public class PermissionGroupingTests
{
    private static ObjectPermissionInfo MakePerm(
        string db = "TestDb", string grantee = "public", string granteeType = "DATABASE_ROLE",
        string classDesc = "OBJECT_OR_COLUMN", string permission = "SELECT", string state = "GRANT",
        string? schema = "dbo", string? obj = "Orders", string? column = null) =>
        new()
        {
            DatabaseName = db, GranteeName = grantee, GranteeType = granteeType, ClassDesc = classDesc,
            PermissionName = permission, StateDesc = state, SchemaName = schema, ObjectName = obj, ColumnName = column
        };

    [Fact]
    public void TargetLabel_ObjectLevelGrant_ReturnsSchemaDotObject()
    {
        var p = MakePerm(obj: "Orders", column: null);
        Assert.Equal("dbo.Orders", PermissionGrouping.TargetLabel(p));
    }

    [Fact]
    public void TargetLabel_ColumnLevelGrant_ReturnsSchemaDotObjectDotColumn()
    {
        var p = MakePerm(obj: "Orders", column: "CustomerEmail");
        Assert.Equal("dbo.Orders.CustomerEmail", PermissionGrouping.TargetLabel(p));
    }

    [Fact]
    public void TargetLabel_SchemaLevelGrant_ReturnsSchemaOnly()
    {
        var p = MakePerm(classDesc: "SCHEMA", obj: null, schema: "dbo");
        Assert.Equal("dbo", PermissionGrouping.TargetLabel(p));
    }

    [Fact]
    public void TargetLabel_DatabaseLevelGrant_ReturnsPlaceholder()
    {
        var p = MakePerm(classDesc: "DATABASE", obj: null, schema: null);
        Assert.Equal("(database-level)", PermissionGrouping.TargetLabel(p));
    }

    [Fact]
    public void Group_ColumnLevelGrantsOnSameTable_DoNotCollapseIntoOneTarget()
    {
        // This is exactly the bug scenario reported: "public" granted SELECT on 3 different columns
        // of the same table used to render as 3 identical-looking rows before minor_id/ColumnName
        // was tracked. They should now group into ONE finding/row with 3 distinct targets, not merge
        // into a single target or (the original bug) look like unrelated exact duplicates.
        var perms = new List<ObjectPermissionInfo>
        {
            MakePerm(column: "CustomerId"),
            MakePerm(column: "CustomerEmail"),
            MakePerm(column: "OrderDate"),
        };

        var groups = PermissionGrouping.Group(perms);

        Assert.Single(groups);
        Assert.Equal(3, groups[0].Targets.Count);
        Assert.Contains("dbo.Orders.CustomerId", groups[0].Targets);
        Assert.Contains("dbo.Orders.CustomerEmail", groups[0].Targets);
        Assert.Contains("dbo.Orders.OrderDate", groups[0].Targets);
    }

    [Fact]
    public void Group_DifferentScopes_NeverMerged()
    {
        var perms = new List<ObjectPermissionInfo>
        {
            MakePerm(classDesc: "OBJECT_OR_COLUMN", obj: "Orders"),
            MakePerm(classDesc: "SCHEMA", obj: null, schema: "dbo"),
        };

        Assert.Equal(2, PermissionGrouping.Group(perms).Count);
    }

    [Fact]
    public void Group_DifferentGrantees_NeverMerged()
    {
        var perms = new List<ObjectPermissionInfo>
        {
            MakePerm(grantee: "public"),
            MakePerm(grantee: "SomeAppUser", granteeType: "SQL_USER"),
        };

        Assert.Equal(2, PermissionGrouping.Group(perms).Count);
    }

    [Fact]
    public void Group_DifferentDatabases_NeverMerged()
    {
        var perms = new List<ObjectPermissionInfo> { MakePerm(db: "DbA"), MakePerm(db: "DbB") };
        Assert.Equal(2, PermissionGrouping.Group(perms).Count);
    }

    [Fact]
    public void TargetSummary_UnderCap_JoinsAll()
    {
        Assert.Equal("a, b, c", PermissionGrouping.TargetSummary(["a", "b", "c"], 5));
    }

    [Fact]
    public void TargetSummary_OverCap_TruncatesWithCount()
    {
        Assert.Equal("a, b, +3 more", PermissionGrouping.TargetSummary(["a", "b", "c", "d", "e"], 2));
    }
}
