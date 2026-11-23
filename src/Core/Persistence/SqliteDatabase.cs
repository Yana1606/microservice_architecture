using Microsoft.Data.Sqlite;

namespace MathTournament.Persistence;

/// <summary>
/// Owns SQLite connection configuration.
/// Each operation receives its own connection.
/// </summary>
internal sealed class SqliteDatabase
{
    private readonly string connectionString;

    public SqliteDatabase(string path)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(path))!);

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            DefaultTimeout = 10
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}