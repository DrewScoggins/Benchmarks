// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Data;
using Crank.PerfLabExporter.Publishing;
using Microsoft.Data.SqlClient;

namespace Crank.PerfLabExporter.Backfill
{
    internal sealed class LegacyTrendSqlException : Exception
    {
        public LegacyTrendSqlException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    internal sealed class SqlLegacyTrendRepository : ILegacyTrendRepository
    {
        private readonly string _connectionString;
        private readonly SqlTableIdentifier _table;
        private readonly ISqlAccessTokenProvider? _tokenProvider;
        private readonly PublicationRetryOptions _retry;
        private readonly IRetryDelay _retryDelay;
        private readonly Action<string>? _log;

        public SqlLegacyTrendRepository(
            string connectionString,
            SqlTableIdentifier table,
            ISqlAccessTokenProvider? tokenProvider,
            PublicationRetryOptions retry,
            IRetryDelay retryDelay,
            Action<string>? log = null)
        {
            if (retry.MaximumAttempts <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retry),
                    "At least one SQL read attempt is required.");
            }

            if (retry.InitialDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(retry),
                    "The SQL retry delay cannot be negative.");
            }

            _connectionString = connectionString;
            _table = table;
            _tokenProvider = tokenProvider;
            _retry = retry;
            _retryDelay = retryDelay;
            _log = log;
        }

        public async Task<IReadOnlyList<LegacyTrendRow>> ReadBatchAsync(
            LegacyTrendQuery query,
            CancellationToken cancellationToken)
        {
            var delay = _retry.InitialDelay;
            for (var attempt = 1; attempt <= _retry.MaximumAttempts; attempt++)
            {
                try
                {
                    return await ReadOnceAsync(query, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception) when (attempt < _retry.MaximumAttempts)
                {
                    _log?.Invoke($"SQL read attempt {attempt} failed; retrying.");
                    await _retryDelay.DelayAsync(delay, cancellationToken);
                    delay += delay;
                }
                catch (Exception exception)
                {
                    throw new LegacyTrendSqlException(
                        $"SQL read failed after {attempt} attempt(s): {exception.Message}",
                        exception);
                }
            }

            throw new InvalidOperationException("The SQL retry loop completed unexpectedly.");
        }

        private async Task<IReadOnlyList<LegacyTrendRow>> ReadOnceAsync(
            LegacyTrendQuery query,
            CancellationToken cancellationToken)
        {
            await using var connection = new SqlConnection(_connectionString);
            if (_tokenProvider is not null)
            {
                connection.AccessToken = await _tokenProvider.GetTokenAsync(cancellationToken);
            }

            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = CreateCommandText(query.After is not null);
            command.Parameters.Add(
                new SqlParameter("@BatchSize", SqlDbType.Int)
                {
                    Value = query.BatchSize
                });
            command.Parameters.Add(
                new SqlParameter("@StartUtc", SqlDbType.DateTime2)
                {
                    Value = query.StartUtc.UtcDateTime
                });
            command.Parameters.Add(
                new SqlParameter("@EndUtc", SqlDbType.DateTime2)
                {
                    Value = query.EndUtc.UtcDateTime
                });
            if (query.After is { } cursor)
            {
                command.Parameters.Add(
                    new SqlParameter("@AfterDateTimeUtc", SqlDbType.DateTime2)
                    {
                        Value = cursor.DateTimeUtc.UtcDateTime
                    });
                command.Parameters.Add(
                    new SqlParameter("@AfterId", SqlDbType.BigInt)
                    {
                        Value = cursor.Id
                    });
            }

            var rows = new List<LegacyTrendRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new LegacyTrendRow(
                    Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToBoolean(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
                    ReadUtcTimestamp(reader.GetValue(2)),
                    ReadString(reader, 3),
                    ReadString(reader, 4),
                    ReadString(reader, 5),
                    ReadString(reader, 6)));
            }

            return rows;
        }

        private string CreateCommandText(bool hasCursor)
        {
            var cursorPredicate = hasCursor
                ? """
                  AND (
                    [DateTimeUtc] > @AfterDateTimeUtc
                    OR ([DateTimeUtc] = @AfterDateTimeUtc AND [Id] > @AfterId)
                  )
                  """
                : string.Empty;
            return
                $"""
                 SELECT TOP (@BatchSize)
                   [Id],
                   [Excluded],
                   [DateTimeUtc],
                   [Session],
                   [Scenario],
                   [Description],
                   [Document]
                 FROM {_table.QuotedName}
                 WHERE [DateTimeUtc] >= @StartUtc
                   AND [DateTimeUtc] <= @EndUtc
                 {cursorPredicate}
                 ORDER BY [DateTimeUtc] ASC, [Id] ASC;
                 """;
        }

        private static DateTimeOffset ReadUtcTimestamp(object value)
        {
            return value switch
            {
                DateTimeOffset timestamp => timestamp.ToUniversalTime(),
                DateTime timestamp => new DateTimeOffset(
                    DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
                _ => throw new InvalidDataException(
                    $"SQL DateTimeUtc had unsupported type '{value.GetType().Name}'.")
            };
        }

        private static string ReadString(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal)
                ? string.Empty
                : reader.GetString(ordinal);
        }
    }
}
