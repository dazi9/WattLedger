using System.Globalization;
using Microsoft.Data.Sqlite;
using WattLedger.Core.Models;

namespace WattLedger.Core.Storage;

public sealed class LedgerDatabase
{
    private readonly string _connectionString;

    public LedgerDatabase(string? databasePath = null)
    {
        DatabasePath = databasePath ?? DefaultDatabasePath;
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public static string DefaultDatabasePath
    {
        get
        {
            return GetDefaultDatabasePath(
                OperatingSystem.IsWindows(),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }

    internal static string GetDefaultDatabasePath(bool isWindows, string localAppData, string? xdgDataHome, string userProfile)
    {
        if (isWindows)
        {
            return Path.Combine(localAppData, "WattLedger", "wattledger.db");
        }

        var dataHome = !string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathFullyQualified(xdgDataHome)
            ? xdgDataHome
            : Path.Combine(
                Path.IsPathFullyQualified(userProfile) ? userProfile : Environment.CurrentDirectory,
                ".local",
                "share");
        return Path.Combine(dataHome, "wattledger", "wattledger.db");
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts INTEGER NOT NULL,
                power_state TEXT NOT NULL,
                battery_percent INTEGER NULL,
                remaining_mwh INTEGER NULL,
                full_charge_mwh INTEGER NULL,
                design_mwh INTEGER NULL,
                voltage_mv INTEGER NULL,
                discharge_mw INTEGER NULL,
                charge_mw INTEGER NULL,
                is_ac INTEGER NOT NULL,
                is_charging INTEGER NOT NULL,
                is_discharging INTEGER NOT NULL,
                rapl_pkg_w REAL NULL,
                rapl_cpu_w REAL NULL,
                rapl_gpu_w REAL NULL,
                rapl_dram_w REAL NULL,
                brightness_pct INTEGER NULL,
                foreground_process TEXT NULL,
                cycle_count INTEGER NULL,
                device_name TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_samples_ts ON samples(ts);

            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                start_ts INTEGER NOT NULL UNIQUE,
                end_ts INTEGER NOT NULL,
                type TEXT NOT NULL,
                note TEXT NULL,
                start_percent INTEGER NULL,
                end_percent INTEGER NULL,
                start_remaining_mwh INTEGER NULL,
                end_remaining_mwh INTEGER NULL,
                energy_wh REAL NOT NULL,
                avg_watts REAL NOT NULL,
                peak_watts REAL NOT NULL,
                duration_seconds INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_start ON sessions(start_ts);

            CREATE TABLE IF NOT EXISTS battery_health (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts INTEGER NOT NULL,
                design_mwh INTEGER NULL,
                full_charge_mwh INTEGER NULL,
                cycle_count INTEGER NULL,
                health_pct REAL NULL
            );

            CREATE INDEX IF NOT EXISTS ix_health_ts ON battery_health(ts);
            """;
        command.ExecuteNonQuery();
    }

    public long InsertSample(SampleRecord sample)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO samples (
                ts, power_state, battery_percent, remaining_mwh, full_charge_mwh, design_mwh,
                voltage_mv, discharge_mw, charge_mw, is_ac, is_charging, is_discharging,
                rapl_pkg_w, rapl_cpu_w, rapl_gpu_w, rapl_dram_w, brightness_pct,
                foreground_process, cycle_count, device_name)
            VALUES (
                $ts, $power_state, $battery_percent, $remaining_mwh, $full_charge_mwh, $design_mwh,
                $voltage_mv, $discharge_mw, $charge_mw, $is_ac, $is_charging, $is_discharging,
                $rapl_pkg_w, $rapl_cpu_w, $rapl_gpu_w, $rapl_dram_w, $brightness_pct,
                $foreground_process, $cycle_count, $device_name);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ts", sample.Timestamp.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$power_state", sample.PowerState);
        AddNullable(command, "$battery_percent", sample.BatteryPercent);
        AddNullable(command, "$remaining_mwh", sample.RemainingMwh);
        AddNullable(command, "$full_charge_mwh", sample.FullChargeMwh);
        AddNullable(command, "$design_mwh", sample.DesignMwh);
        AddNullable(command, "$voltage_mv", sample.VoltageMv);
        AddNullable(command, "$discharge_mw", sample.DischargeMw);
        AddNullable(command, "$charge_mw", sample.ChargeMw);
        command.Parameters.AddWithValue("$is_ac", sample.IsAcOnline ? 1 : 0);
        command.Parameters.AddWithValue("$is_charging", sample.IsCharging ? 1 : 0);
        command.Parameters.AddWithValue("$is_discharging", sample.IsDischarging ? 1 : 0);
        AddNullable(command, "$rapl_pkg_w", sample.RaplPackageWatts);
        AddNullable(command, "$rapl_cpu_w", sample.RaplCpuWatts);
        AddNullable(command, "$rapl_gpu_w", sample.RaplGpuWatts);
        AddNullable(command, "$rapl_dram_w", sample.RaplDramWatts);
        AddNullable(command, "$brightness_pct", sample.BrightnessPercent);
        AddNullable(command, "$foreground_process", sample.ForegroundProcess);
        AddNullable(command, "$cycle_count", sample.CycleCount);
        AddNullable(command, "$device_name", sample.DeviceName);

        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return id;
    }

    public void UpsertSession(SessionRecord session)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (
                start_ts, end_ts, type, note, start_percent, end_percent,
                start_remaining_mwh, end_remaining_mwh, energy_wh, avg_watts,
                peak_watts, duration_seconds)
            VALUES (
                $start_ts, $end_ts, $type, $note, $start_percent, $end_percent,
                $start_remaining_mwh, $end_remaining_mwh, $energy_wh, $avg_watts,
                $peak_watts, $duration_seconds)
            ON CONFLICT(start_ts) DO UPDATE SET
                end_ts = excluded.end_ts,
                type = excluded.type,
                note = excluded.note,
                start_percent = excluded.start_percent,
                end_percent = excluded.end_percent,
                start_remaining_mwh = excluded.start_remaining_mwh,
                end_remaining_mwh = excluded.end_remaining_mwh,
                energy_wh = excluded.energy_wh,
                avg_watts = excluded.avg_watts,
                peak_watts = excluded.peak_watts,
                duration_seconds = excluded.duration_seconds;
            """;
        command.Parameters.AddWithValue("$start_ts", session.Start.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end_ts", session.End.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$type", session.Type);
        AddNullable(command, "$note", session.Note);
        AddNullable(command, "$start_percent", session.StartPercent);
        AddNullable(command, "$end_percent", session.EndPercent);
        AddNullable(command, "$start_remaining_mwh", session.StartRemainingMwh);
        AddNullable(command, "$end_remaining_mwh", session.EndRemainingMwh);
        command.Parameters.AddWithValue("$energy_wh", session.EnergyWh);
        command.Parameters.AddWithValue("$avg_watts", session.AverageWatts);
        command.Parameters.AddWithValue("$peak_watts", session.PeakWatts);
        command.Parameters.AddWithValue("$duration_seconds", session.DurationSeconds);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<SessionRecord> GetSessions(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, start_ts, end_ts, type, note, start_percent, end_percent,
                   start_remaining_mwh, end_remaining_mwh, energy_wh, avg_watts,
                   peak_watts, duration_seconds
            FROM sessions
            WHERE end_ts >= $from AND start_ts <= $to
            ORDER BY start_ts;
            """;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

        var results = new List<SessionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SessionRecord
            {
                Id = reader.GetInt64(0),
                Start = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                End = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                Type = reader.GetString(3),
                Note = GetNullableString(reader, 4),
                StartPercent = GetNullableInt(reader, 5),
                EndPercent = GetNullableInt(reader, 6),
                StartRemainingMwh = GetNullableLong(reader, 7),
                EndRemainingMwh = GetNullableLong(reader, 8),
                EnergyWh = reader.GetDouble(9),
                AverageWatts = reader.GetDouble(10),
                PeakWatts = reader.GetDouble(11)
            });
        }

        return results;
    }

    public IReadOnlyList<SampleRecord> GetSamples(DateTimeOffset from, DateTimeOffset to, int maxPoints = 0)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ts, power_state, battery_percent, remaining_mwh, full_charge_mwh,
                   design_mwh, voltage_mv, discharge_mw, charge_mw, is_ac, is_charging,
                   is_discharging, rapl_pkg_w, rapl_cpu_w, rapl_gpu_w, rapl_dram_w,
                   brightness_pct, foreground_process, cycle_count, device_name
            FROM samples
            WHERE ts >= $from AND ts <= $to
            ORDER BY ts;
            """;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

        var results = new List<SampleRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadSample(reader));
        }

        if (maxPoints > 0 && results.Count > maxPoints)
        {
            return Decimate(results, maxPoints);
        }

        return results;
    }

    public SampleRecord? GetLatestSample()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ts, power_state, battery_percent, remaining_mwh, full_charge_mwh,
                   design_mwh, voltage_mv, discharge_mw, charge_mw, is_ac, is_charging,
                   is_discharging, rapl_pkg_w, rapl_cpu_w, rapl_gpu_w, rapl_dram_w,
                   brightness_pct, foreground_process, cycle_count, device_name
            FROM samples ORDER BY ts DESC LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSample(reader) : null;
    }

    public SampleRecord? GetLatestSample(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, ts, power_state, battery_percent, remaining_mwh, full_charge_mwh,
                   design_mwh, voltage_mv, discharge_mw, charge_mw, is_ac, is_charging,
                   is_discharging, rapl_pkg_w, rapl_cpu_w, rapl_gpu_w, rapl_dram_w,
                   brightness_pct, foreground_process, cycle_count, device_name
            FROM samples
            WHERE ts >= $from AND ts <= $to
            ORDER BY ts DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSample(reader) : null;
    }

    public void InsertHealth(HealthRecord health)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO battery_health (ts, design_mwh, full_charge_mwh, cycle_count, health_pct)
            VALUES ($ts, $design_mwh, $full_charge_mwh, $cycle_count, $health_pct);
            """;
        command.Parameters.AddWithValue("$ts", health.Timestamp.ToUnixTimeMilliseconds());
        AddNullable(command, "$design_mwh", health.DesignMwh);
        AddNullable(command, "$full_charge_mwh", health.FullChargeMwh);
        AddNullable(command, "$cycle_count", health.CycleCount);
        AddNullable(command, "$health_pct", health.HealthPercent);
        command.ExecuteNonQuery();
    }

    public HealthRecord? GetLatestHealth()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts, design_mwh, full_charge_mwh, cycle_count, health_pct
            FROM battery_health ORDER BY ts DESC LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new HealthRecord
        {
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
            DesignMwh = GetNullableLong(reader, 1),
            FullChargeMwh = GetNullableLong(reader, 2),
            CycleCount = GetNullableInt(reader, 3),
            HealthPercent = GetNullableDouble(reader, 4)
        };
    }

    public IReadOnlyList<HealthRecord> GetHealthHistory(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts, design_mwh, full_charge_mwh, cycle_count, health_pct
            FROM battery_health
            WHERE ts >= $from AND ts <= $to
            ORDER BY ts;
            """;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

        var results = new List<HealthRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new HealthRecord
            {
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                DesignMwh = GetNullableLong(reader, 1),
                FullChargeMwh = GetNullableLong(reader, 2),
                CycleCount = GetNullableInt(reader, 3),
                HealthPercent = GetNullableDouble(reader, 4)
            });
        }

        return results;
    }

    public IReadOnlyList<DailySummary> GetDailySummaries(DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT date(start_ts / 1000, 'unixepoch', 'localtime') AS day,
                   SUM(CASE WHEN type = 'Battery' AND energy_wh > 0 THEN energy_wh ELSE 0 END) AS active_wh,
                   SUM(CASE WHEN type IN ('ModernStandby', 'SleepGap') AND energy_wh > 0 THEN energy_wh ELSE 0 END) AS standby_wh,
                   SUM(CASE WHEN energy_wh < 0 THEN -energy_wh ELSE 0 END) AS charge_wh,
                   SUM(CASE WHEN type = 'Battery' THEN duration_seconds ELSE 0 END) AS active_seconds,
                   SUM(CASE WHEN type IN ('ModernStandby', 'SleepGap') THEN duration_seconds ELSE 0 END) AS standby_seconds,
                   SUM(CASE WHEN type IN ('ModernStandby', 'SleepGap') AND note LIKE '%battery energy unavailable%' THEN 1 ELSE 0 END) AS standby_energy_incomplete
            FROM sessions
            WHERE end_ts >= $from AND start_ts <= $to
            GROUP BY day
            ORDER BY day;
            """;
        command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

        var results = new List<DailySummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var dayText = reader.GetString(0);
            if (!DateOnly.TryParseExact(dayText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                continue;
            }

            results.Add(new DailySummary
            {
                Date = day,
                ActiveWh = reader.GetDouble(1),
                StandbyWh = reader.GetDouble(2),
                ChargeWh = reader.GetDouble(3),
                ActiveSeconds = reader.GetDouble(4),
                StandbySeconds = reader.GetDouble(5),
                StandbyEnergyIncomplete = reader.GetInt64(6) > 0
            });
        }

        return results;
    }

    public long GetSampleCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM samples;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public long GetSessionCount()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void PruneSamples(DateTimeOffset before)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM samples WHERE ts < $before;";
        command.Parameters.AddWithValue("$before", before.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void AddNullable(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static IReadOnlyList<SampleRecord> Decimate(List<SampleRecord> samples, int maxPoints)
    {
        var result = new List<SampleRecord>(maxPoints + 1);
        var step = (double)samples.Count / maxPoints;
        for (var i = 0; i < maxPoints; i++)
        {
            result.Add(samples[(int)(i * step)]);
        }

        if (result.Count == 0 || !ReferenceEquals(result[^1], samples[^1]))
        {
            result.Add(samples[^1]);
        }

        return result;
    }

    private static SampleRecord ReadSample(SqliteDataReader reader)
    {
        return new SampleRecord
        {
            Id = reader.GetInt64(0),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
            PowerState = reader.GetString(2),
            BatteryPercent = GetNullableInt(reader, 3),
            RemainingMwh = GetNullableLong(reader, 4),
            FullChargeMwh = GetNullableLong(reader, 5),
            DesignMwh = GetNullableLong(reader, 6),
            VoltageMv = GetNullableInt(reader, 7),
            DischargeMw = GetNullableLong(reader, 8),
            ChargeMw = GetNullableLong(reader, 9),
            IsAcOnline = reader.GetInt32(10) != 0,
            IsCharging = reader.GetInt32(11) != 0,
            IsDischarging = reader.GetInt32(12) != 0,
            RaplPackageWatts = GetNullableDouble(reader, 13),
            RaplCpuWatts = GetNullableDouble(reader, 14),
            RaplGpuWatts = GetNullableDouble(reader, 15),
            RaplDramWatts = GetNullableDouble(reader, 16),
            BrightnessPercent = GetNullableInt(reader, 17),
            ForegroundProcess = GetNullableString(reader, 18),
            CycleCount = GetNullableInt(reader, 19),
            DeviceName = GetNullableString(reader, 20)
        };
    }

    private static int? GetNullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? GetNullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? GetNullableDouble(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
