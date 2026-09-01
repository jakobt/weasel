using System.Collections;
using System.Data;
using System.Data.Common;
using JasperFx;
using Shouldly;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.MySql;
using Weasel.Oracle;
using Weasel.Postgresql;
using Weasel.Sqlite;
using Weasel.SqlServer;
using Xunit;

namespace Weasel.Core.Tests;

/// <summary>
///     Weasel used to leave every command it issued on the driver's 30 second default, so a large
///     enough schema died with "Execution Timeout Expired" during introspection and created nothing.
///     <see cref="Migrator.CommandTimeout" /> is the one knob that fixes it, and it has to reach both
///     the introspection command and the DDL commands.
/// </summary>
public class command_timeout_is_honoured_by_every_provider
{
    private static Migrator migratorFor(string provider) => provider switch
    {
        "postgresql" => new PostgresqlMigrator(),
        "sqlserver" => new SqlServerMigrator(),
        "mysql" => new MySqlMigrator(),
        "sqlite" => new SqliteMigrator(),
        "oracle" => new OracleMigrator(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    [InlineData("oracle")]
    public async Task ddl_commands_carry_the_configured_timeout(string provider)
    {
        var migrator = migratorFor(provider);
        migrator.CommandTimeout = 600;

        await using var conn = new RecordingConnection();
        await migrator.ApplyAllAsync(conn, migration(), AutoCreate.All, new SilentLogger());

        conn.ExecutedTimeouts.ShouldNotBeEmpty();
        conn.ExecutedTimeouts.ShouldAllBe(x => x == 600);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    [InlineData("oracle")]
    public async Task ddl_commands_are_left_alone_when_nothing_is_configured(string provider)
    {
        await using var conn = new RecordingConnection();
        await migratorFor(provider).ApplyAllAsync(conn, migration(), AutoCreate.All, new SilentLogger());

        conn.ExecutedTimeouts.ShouldNotBeEmpty();
        conn.ExecutedTimeouts.ShouldAllBe(x => x == RecordingCommand.FromConnectionString);
    }

    // Oracle is absent by necessity, not by oversight: OracleMigrator.CreateCommandBuilder hands back
    // a real OracleCommand, which will not accept a stand-in connection. Its own path is covered by
    // oracle_carries_the_timeout_onto_every_split_command below.
    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    public async Task the_introspection_command_carries_the_configured_timeout(string provider)
    {
        var migrator = migratorFor(provider);
        migrator.CommandTimeout = 600;

        await using var conn = new RecordingConnection();
        await SchemaMigration.DetermineAsync(conn, migrator, CancellationToken.None, new StubSchemaObject());

        conn.ExecutedTimeouts.ShouldBe(new[] { 600 });
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    public async Task the_introspection_command_is_left_alone_when_nothing_is_configured(string provider)
    {
        await using var conn = new RecordingConnection();
        await SchemaMigration.DetermineAsync(conn, migratorFor(provider), CancellationToken.None,
            new StubSchemaObject());

        conn.ExecutedTimeouts.ShouldBe(new[] { RecordingCommand.FromConnectionString });
    }

    [Fact]
    public void oracle_carries_the_timeout_onto_every_split_command()
    {
        var builder = new OracleDbCommandBuilder();
        builder.Command.CommandTimeout = 600;

        builder.Append("select 1 from dual");
        builder.StartNewCommand();
        builder.Append("select 2 from dual");

        var commands = builder.CompileCommands();

        commands.Count.ShouldBe(2);
        commands.ShouldAllBe(x => x.CommandTimeout == 600);
    }

    [Fact]
    public async Task rollback_commands_carry_the_configured_timeout()
    {
        var migrator = new PostgresqlMigrator { CommandTimeout = 600 };

        await using var conn = new RecordingConnection();
        await migration().RollbackAllAsync(conn, migrator);

        conn.ExecutedTimeouts.ShouldBe(new[] { 600 });
    }

    [Fact]
    public void a_command_is_untouched_when_no_timeout_is_configured()
    {
        var command = new RecordingCommand(null);

        new SqlServerMigrator().ApplyCommandTimeout(command);

        command.CommandTimeout.ShouldBe(RecordingCommand.FromConnectionString);
    }

    [Fact]
    public async Task fingerprint_bookkeeping_carries_the_configured_timeout()
    {
        var migrator = new SqlServerMigrator { CommandTimeout = 600 };

        await using var conn = new RecordingConnection();
        await SchemaFingerprint.HasStampAsync(conn, migrator, "abc", CancellationToken.None);
        await SchemaFingerprint.RecordAsync(conn, migrator, "abc", CancellationToken.None);

        conn.ExecutedTimeouts.Count.ShouldBeGreaterThan(1);
        conn.ExecutedTimeouts.ShouldAllBe(x => x == 600);
    }

    [Fact]
    public async Task fingerprint_bookkeeping_is_left_alone_when_nothing_is_configured()
    {
        var migrator = new SqlServerMigrator();

        await using var conn = new RecordingConnection();
        await SchemaFingerprint.HasStampAsync(conn, migrator, "abc", CancellationToken.None);
        await SchemaFingerprint.RecordAsync(conn, migrator, "abc", CancellationToken.None);

        conn.ExecutedTimeouts.Count.ShouldBeGreaterThan(1);
        conn.ExecutedTimeouts.ShouldAllBe(x => x == RecordingCommand.FromConnectionString);
    }

    [Fact]
    public async Task ensure_database_exists_carries_the_configured_timeout()
    {
        var migrator = new OracleMigrator { CommandTimeout = 600 };

        await using var conn = new RecordingConnection("User Id=weasel;Password=x;Data Source=fake");
        await migrator.EnsureDatabaseExistsAsync(conn);

        conn.ExecutedTimeouts.ShouldBe(new[] { 600 });
    }

    [Fact]
    public async Task ensure_database_exists_is_left_alone_when_nothing_is_configured()
    {
        await using var conn = new RecordingConnection("User Id=weasel;Password=x;Data Source=fake");
        await new OracleMigrator().EnsureDatabaseExistsAsync(conn);

        conn.ExecutedTimeouts.ShouldBe(new[] { RecordingCommand.FromConnectionString });
    }

    private static SchemaMigration migration() => new(new StubDelta());

    #region Test stubs

    private class SilentLogger: IMigrationLogger
    {
        public void SchemaChange(string sql)
        {
        }

        public void OnFailure(DbCommand command, Exception ex) => throw ex;
    }

    private class StubSchemaObject: ISchemaObject
    {
        public DbObjectName Identifier { get; } = new("dbo", "widgets");

        public void WriteCreateStatement(Migrator migrator, TextWriter writer) =>
            writer.WriteLine("create table widgets (id int);");

        public void WriteDropStatement(Migrator rules, TextWriter writer) =>
            writer.WriteLine("drop table widgets;");

        public void ConfigureQueryCommand(DbCommandBuilder builder) => builder.Append("select 1;");

        public Task<ISchemaObjectDelta> CreateDeltaAsync(DbDataReader reader, CancellationToken ct = default) =>
            Task.FromResult<ISchemaObjectDelta>(new StubDelta());

        public IEnumerable<DbObjectName> AllNames()
        {
            yield return Identifier;
        }
    }

    private class StubDelta: ISchemaObjectDelta
    {
        public ISchemaObject SchemaObject { get; } = new StubSchemaObject();
        public SchemaPatchDifference Difference => SchemaPatchDifference.Create;

        public void WriteUpdate(Migrator rules, TextWriter writer) =>
            SchemaObject.WriteCreateStatement(rules, writer);

        public void WriteRollback(Migrator rules, TextWriter writer) =>
            SchemaObject.WriteDropStatement(rules, writer);

        public void WriteRestorationOfPreviousState(Migrator rules, TextWriter writer)
        {
        }
    }

    private sealed class RecordingConnection: DbConnection
    {
        public RecordingConnection(string connectionString = "Data Source=fake")
        {
            ConnectionString = connectionString;
        }

        public List<int> ExecutedTimeouts { get; } = new();

        public override string ConnectionString { get; set; }
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
    }

    private sealed class RecordingCommand: DbCommand
    {
        /// <summary>
        ///     What the driver seeds a fresh command's timeout with when the connection string carries
        ///     <c>Command Timeout=900</c>. Deliberately not 30: a negative test that asserted the
        ///     driver's own default would pass just as happily against a Weasel that had stamped 30
        ///     onto the command itself, which is the mistake this whole setting exists to avoid.
        /// </summary>
        public const int FromConnectionString = 900;

        private readonly RecordingConnection? _connection;

        public RecordingCommand(RecordingConnection? connection)
        {
            _connection = connection;
            DbConnection = connection;
        }

        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; } = FromConnectionString;
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection { get; } = new RecordingParameterCollection();

        public override void Cancel()
        {
        }

        public override void Prepare()
        {
        }

        public override int ExecuteNonQuery()
        {
            _connection?.ExecutedTimeouts.Add(CommandTimeout);
            return 0;
        }

        public override object ExecuteScalar()
        {
            _connection?.ExecutedTimeouts.Add(CommandTimeout);
            return 0;
        }

        protected override DbParameter CreateDbParameter() => new RecordingParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            _connection?.ExecutedTimeouts.Add(CommandTimeout);
            return new EmptyReader();
        }
    }

    private sealed class RecordingParameterCollection: DbParameterCollection
    {
        private readonly List<DbParameter> _parameters = new();

        public override int Count => _parameters.Count;
        public override object SyncRoot { get; } = new();

        public override int Add(object value)
        {
            _parameters.Add((DbParameter)value);
            return _parameters.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values) Add(value!);
        }

        public override void Clear() => _parameters.Clear();
        public override bool Contains(object value) => _parameters.Contains((DbParameter)value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();
        public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _parameters.FindIndex(x => x.ParameterName == parameterName);
        public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);
        public override void Remove(object value) => _parameters.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _parameters.RemoveAt(index);
        public override void RemoveAt(string parameterName) => RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => _parameters[index];
        protected override DbParameter GetParameter(string parameterName) => _parameters[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

        protected override void SetParameter(string parameterName, DbParameter value) =>
            _parameters[IndexOf(parameterName)] = value;
    }

    private sealed class RecordingParameter: DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }

        public override void ResetDbType()
        {
        }
    }

    private sealed class EmptyReader: DbDataReader
    {
        public override int Depth => 0;
        public override int FieldCount => 0;
        public override bool HasRows => false;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override object this[int ordinal] => throw new IndexOutOfRangeException();
        public override object this[string name] => throw new IndexOutOfRangeException();

        public override bool GetBoolean(int ordinal) => throw new IndexOutOfRangeException();
        public override byte GetByte(int ordinal) => throw new IndexOutOfRangeException();

        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
            throw new IndexOutOfRangeException();

        public override char GetChar(int ordinal) => throw new IndexOutOfRangeException();

        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
            throw new IndexOutOfRangeException();

        public override string GetDataTypeName(int ordinal) => throw new IndexOutOfRangeException();
        public override DateTime GetDateTime(int ordinal) => throw new IndexOutOfRangeException();
        public override decimal GetDecimal(int ordinal) => throw new IndexOutOfRangeException();
        public override double GetDouble(int ordinal) => throw new IndexOutOfRangeException();
        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
        public override Type GetFieldType(int ordinal) => throw new IndexOutOfRangeException();
        public override float GetFloat(int ordinal) => throw new IndexOutOfRangeException();
        public override Guid GetGuid(int ordinal) => throw new IndexOutOfRangeException();
        public override short GetInt16(int ordinal) => throw new IndexOutOfRangeException();
        public override int GetInt32(int ordinal) => throw new IndexOutOfRangeException();
        public override long GetInt64(int ordinal) => throw new IndexOutOfRangeException();
        public override string GetName(int ordinal) => throw new IndexOutOfRangeException();
        public override int GetOrdinal(string name) => throw new IndexOutOfRangeException();
        public override string GetString(int ordinal) => throw new IndexOutOfRangeException();
        public override object GetValue(int ordinal) => throw new IndexOutOfRangeException();
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => true;
        public override bool NextResult() => false;
        public override bool Read() => false;
    }

    #endregion
}
