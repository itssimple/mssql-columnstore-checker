using Microsoft.Data.SqlClient;

namespace ColumnstoreAnalyzer;

/// <summary>Shared connection-string/auth logic so every DB-touching class builds connections the same way.</summary>
internal static class ConnectionFactory
{
    public static SqlConnection Open(AnalyzerOptions opt)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = opt.Server,
            InitialCatalog = opt.Database,
            TrustServerCertificate = opt.TrustServerCertificate,
            ApplicationName = "ColumnstoreAnalyzer",
            ConnectTimeout = 30
        };
        if (!string.IsNullOrEmpty(opt.User))
        {
            b.UserID = opt.User;
            b.Password = opt.Password ?? "";
        }
        else
        {
            b.IntegratedSecurity = true;
        }

        var conn = new SqlConnection(b.ConnectionString);
        conn.Open();
        return conn;
    }
}
