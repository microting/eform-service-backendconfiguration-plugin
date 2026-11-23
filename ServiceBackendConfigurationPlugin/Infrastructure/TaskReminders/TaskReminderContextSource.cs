/*
The MIT License (MIT)

Copyright (c) 2007 - 2026 Microting A/S

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microting.EformBackendConfigurationBase.Infrastructure.Data;
using MySqlConnector;

namespace ServiceBackendConfigurationPlugin.Infrastructure.TaskReminders;

/// <summary>
/// Short-lived backend-configuration contexts for the reminder loop, on options built once. The shared
/// context factory runs a synchronous, non-cancellable <c>ServerVersion.AutoDetect</c> (a connection
/// and a query) for EVERY context; at one tick every 2 seconds that would double the idle cost and
/// could block the timer thread. Here the server version is read once, asynchronously and
/// cancellably, and cached; a failed detection is simply tried again on the next call. Connect and
/// command timeouts of this copy of the connection string are capped. No retrying execution
/// strategy: the loop's own back-off is the retry.
/// </summary>
public sealed class TaskReminderContextSource
{
    private const uint TimeoutSeconds = 15;

    private readonly string _connectionString;
    private DbContextOptions<BackendConfigurationPnDbContext> _options;

    public TaskReminderContextSource(string connectionString)
    {
        _connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            ConnectionTimeout = TimeoutSeconds,
            DefaultCommandTimeout = TimeoutSeconds
        }.ConnectionString;
    }

    public async Task<BackendConfigurationPnDbContext> CreateAsync(CancellationToken cancellationToken)
    {
        var options = Volatile.Read(ref _options);
        if (options == null)
        {
            string version;
            await using (var connection = new MySqlConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT VERSION()";
                version = Convert.ToString(
                    await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            // As BackendConfigurationPnContextFactory: a MariaDB server version.
            options = new DbContextOptionsBuilder<BackendConfigurationPnDbContext>()
                .UseMySql(_connectionString, new MariaDbServerVersion(ServerVersion.Parse(version)))
                .Options;
            Volatile.Write(ref _options, options);
        }

        return new BackendConfigurationPnDbContext(options);
    }
}
