namespace ColumnstoreAnalyzer;

/// <summary>
/// Pinned metadata for the opt-in auto-install feature (--health-check --install-missing-tools).
/// Deliberately NOT "always fetch latest": the URL/version/checksum below were captured and
/// verified against the actual upstream file at build time. Behavior is reproducible and the
/// content has been reviewed once, rather than re-fetched blind on every run.
///
/// Notably, Brent Ozar's own 2026 First Responder Kit release notes explicitly warn against
/// auto-fetching-and-running arbitrary code from the internet against SQL Server ("that's how
/// supply chain attacks happen") - which is exactly the risk this pinning + checksum model exists
/// to close off. Re-verify and update the checksums here deliberately when intentionally
/// upgrading; never widen this to "latest" without re-adding the same rigor.
/// </summary>
internal sealed record InstallSource(string Component, string PinnedVersion, string ScriptUrl, string Sha256Hex, string Notes);

internal static class InstallSources
{
    public static readonly InstallSource FirstResponderKit = new(
        Component: "First Responder Kit (sp_Blitz family)",
        PinnedVersion: "20260708",
        ScriptUrl: "https://raw.githubusercontent.com/BrentOzarULTD/SQL-Server-First-Responder-Kit/20260708/Install-All-Scripts.sql",
        Sha256Hex: "726ba3728d9b53287bbed10af03a4e4f23d76f2117e2a73b527c2d27d1f5ea90",
        Notes: "Creates sp_Blitz, sp_BlitzIndex, sp_BlitzCache, sp_BlitzFirst, sp_BlitzLock, sp_BlitzBackups, " +
               "sp_BlitzWho, sp_ineachdb and other FRK objects in the tools database. Batch-separated with " +
               "'GO' on its own line - split and execute each batch (Microsoft.Data.SqlClient does not understand GO).");

    public static readonly InstallSource OlaHallengrenMaintenanceSolution = new(
        Component: "Ola Hallengren Maintenance Solution",
        PinnedVersion: "2026-07-19 18:23:28",
        ScriptUrl: "https://ola.hallengren.com/scripts/MaintenanceSolution.sql",
        Sha256Hex: "f225046a16c87a688046a3d1561cdcea9eb103bfa4f93d2fa58baeca81a4b14b",
        Notes: "Creates CommandExecute/DatabaseBackup/DatabaseIntegrityCheck/IndexOptimize procs AND SQL Agent " +
               "jobs. The script's @BackupDirectory defaults to NULL (instance default backup path) - review " +
               "before running if a specific backup path/retention policy is required. Also batch-separated with 'GO'.");
}
