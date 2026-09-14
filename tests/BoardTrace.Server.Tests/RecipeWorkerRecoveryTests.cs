using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Server.Recipes;
using BoardTrace.Server.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Xunit.Abstractions;

namespace BoardTrace.Server.Tests;

public sealed class RecipeWorkerRecoveryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("idle")]
    [InlineData("startup")]
    [InlineData("progress")]
    [InlineData("completion")]
    public async Task DatabaseInterruptionKeepsHostAliveAndResumesOnlyNewWork(string interruption)
    {
        await using var server = await RecoveryServer.CreateAsync(interruption, output);
        await server.Fault.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await server.SetOnline(false);
        server.Fault.Release.TrySetResult();
        await server.Fault.SqlFailure.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(1000);
        Assert.False(server.Lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client.GetAsync("/api/worker-recovery-alive")).StatusCode);
        await server.SetOnline(true);

        if (server.InterruptedId is Guid interrupted)
        {
            var old = await server.WaitForFinished(interrupted);
            Assert.NotNull(old.CompletedAt);
            if (interruption == "completion")
            {
                Assert.Equal("Completed", old.Status);
                Assert.Equal(200, old.Processed);
                Assert.Equal(server.Fault.CommittedReport, old.ReportJson);
                Assert.Null(old.Error);
            }
            else
            {
                Assert.Equal("Failed", old.Status);
                Assert.Contains("中断", old.Error);
                Assert.Null(old.ReportJson);
                Assert.True(old.Processed < 200);
            }
        }
        var queued = await server.Queue();
        var completed = await server.WaitForFinished(queued);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(200, completed.Processed);
        var report = JsonSerializer.Deserialize<RecipeValidationReport>(completed.ReportJson!)!;
        Assert.Equal(200, report.Rows.Count);
        Assert.Equal(0, report.ExecutionFailures);
        Assert.All(report.Rows, row => Assert.Equal("Completed", row.Status));
        if (interruption == "completion")
        {
            // Check again after recovery processed subsequent work, so an early read
            // cannot hide the worker later overwriting a committed completion.
            var preserved = await server.WaitForFinished(server.InterruptedId!.Value);
            Assert.Equal("Completed", preserved.Status);
            Assert.Equal(server.Fault.CommittedReport, preserved.ReportJson);
            var priorReport = JsonSerializer.Deserialize<RecipeValidationReport>(preserved.ReportJson!)!;
            Assert.Equal(200, priorReport.Rows.Count);
            Assert.Equal(0, priorReport.ExecutionFailures);
        }
        Assert.False(server.Lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task HostCanStopWhileWorkerWaitsForDatabaseRecovery()
    {
        await using var server = await RecoveryServer.CreateAsync("idle", output);
        await server.Fault.Reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await server.SetOnline(false);
        server.Fault.Release.TrySetResult();
        await server.Fault.SqlFailure.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(1000);
        Assert.False(server.Lifetime.ApplicationStopping.IsCancellationRequested);
        await server.Stop().WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Interceptors only hold the real operation until this test takes its own database
    // OFFLINE. No fabricated SqlException, detector result, or worker implementation.
    private sealed class FaultPoint(string point) : ILoggerProvider
    {
        private int claimed;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SqlFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? CommittedReport { get; private set; }

        private async Task Pause(CancellationToken token)
        {
            if (Interlocked.Exchange(ref claimed, 1) != 0) return;
            Reached.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
        public DbCommandInterceptor Commands() => new CommandPause(this, point);
        public SaveChangesInterceptor Saves() => new SavePause(this, point);
        public ILogger CreateLogger(string categoryName) => new FaultLogger(this);
        public void Dispose() { }

        private sealed class CommandPause(FaultPoint fault, string point) : DbCommandInterceptor
        {
            public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                if (command.CommandText.Contains("ValidationRuns", StringComparison.Ordinal) &&
                    ((point == "idle" && command.CommandText.Contains("SELECT TOP(1)", StringComparison.Ordinal) && command.CommandText.Contains("N'Queued'", StringComparison.Ordinal)) ||
                     (point == "startup" && command.CommandText.Contains("N'Running'", StringComparison.Ordinal))))
                    await fault.Pause(cancellationToken);
                return result;
            }
        }

        private sealed class SavePause(FaultPoint fault, string point) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
                InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (point == "progress" && eventData.Context!.ChangeTracker.Entries<ValidationRun>()
                    .Any(entry => entry.Entity.Status == "Running" && entry.Entity.Processed == 1))
                    await fault.Pause(cancellationToken);
                return result;
            }

            public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
                int result, CancellationToken cancellationToken = default)
            {
                var db = eventData.Context!;
                var completed = db.ChangeTracker.Entries<ValidationRun>()
                    .FirstOrDefault(entry => entry.Entity.Status == "Completed" && entry.Entity.Processed == 200)?.Entity;
                if (point == "completion" && completed is not null && fault.CommittedReport is null)
                {
                    // The 200-row report has really committed. Return a real SQL error
                    // from this context/connection before SaveChanges reaches its caller.
                    fault.CommittedReport = completed.ReportJson;
                    await db.Database.OpenConnectionAsync(cancellationToken);
                    await fault.Pause(cancellationToken);
                    await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
                }
                return result;
            }
        }

        private sealed class FaultLogger(FaultPoint fault) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                for (var current = exception; current is not null; current = current.InnerException)
                    if (current is SqlException) { fault.SqlFailure.TrySetResult(); break; }
            }
        }
    }

    private sealed class RecoveryServer : IAsyncDisposable
    {
        private ITestOutputHelper output = null!;
        private const string Prefix = "BoardTrace_WorkerRecovery_";
        private const string Master = "Server=(localdb)\\BoardTrace;Database=master;Integrated Security=true;TrustServerCertificate=true";
        private readonly string database = Prefix + Guid.NewGuid().ToString("N");
        private readonly string directory = Path.Combine(Path.GetTempPath(), "BoardTrace_WorkerRecovery_" + Guid.NewGuid().ToString("N"));
        private WebApplicationFactory<Program>? factory;
        private string inputHash = "", truthHash = "";
        private Guid draftId;
        private bool online = true;
        public FaultPoint Fault { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public Guid? InterruptedId { get; private set; }
        public IHostApplicationLifetime Lifetime { get; private set; } = null!;
        private string Connection => $"Server=(localdb)\\BoardTrace;Database={database};Integrated Security=true;TrustServerCertificate=true;Connect Timeout=1;ConnectRetryCount=0";
        // Observe recovery through a separate connection, outside SqlClient's cached
        // login-failure blocking period for the worker's pool after OFFLINE.
        private BoardTraceDbContext Context() => new(new DbContextOptionsBuilder<BoardTraceDbContext>()
            .UseSqlServer(new SqlConnectionStringBuilder(Connection) { Pooling = false }.ConnectionString,
                options => options.CommandTimeout(2)).Options);

        public static async Task<RecoveryServer> CreateAsync(string interruption, ITestOutputHelper output)
        {
            var server = new RecoveryServer { Fault = new FaultPoint(interruption), output = output };
            await server.Sql($"CREATE DATABASE [{server.database}]");
            try
            {
                await server.CreateImages();
                // Schema preparation is outside the injected outage. Do not use the
                // observer's one-second login/two-second command budget for database DDL.
                await using (var db = new BoardTraceDbContext(new DbContextOptionsBuilder<BoardTraceDbContext>()
                    .UseSqlServer(new SqlConnectionStringBuilder(server.Connection) { ConnectTimeout = 15 }.ConnectionString).Options))
                {
                    await db.Database.EnsureCreatedAsync();
                    server.draftId = Guid.NewGuid();
                    db.RecipeDrafts.Add(new RecipeDraft { Id = server.draftId, Name = "isolated recovery fixture",
                        DefinitionJson = JsonSerializer.Serialize<RecipeDefinition>(new ClassicalRecipeDefinition(new RecipeClassicalSettings())),
                        TargetsJson = JsonSerializer.Serialize(new RecipeTargets(0, 0, 10000)),
                        DataManifestSha256 = server.inputHash, SnapshotHash = new string('a', 64),
                        AuthorId = "isolated-worker-test", UpdatedAt = DateTimeOffset.UtcNow });
                    await db.SaveChangesAsync();
                }
                if (interruption is "startup" or "progress" or "completion")
                    server.InterruptedId = await server.Queue(interruption == "startup" ? "Running" : "Queued");
                server.factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                        ["ConnectionStrings:BoardTrace"] = server.Connection,
                        ["RecipeValidation:DataRoot"] = Path.Combine(server.directory, "data"),
                        ["RecipeValidation:ManifestRoot"] = Path.Combine(server.directory, "manifests") }));
                    builder.ConfigureServices(services => services.AddDbContext<BoardTraceDbContext>(options =>
                        options.AddInterceptors(server.Fault.Commands(), server.Fault.Saves())));
                    builder.ConfigureLogging(logging => logging.AddProvider(server.Fault));
                });
                server.Client = server.factory.CreateClient();
                server.Lifetime = server.factory.Services.GetRequiredService<IHostApplicationLifetime>();
                return server;
            }
            catch { await server.DisposeAsync(); throw; }
        }

        public async Task<Guid> Queue(string status = "Queued")
        {
            var id = Guid.NewGuid();
            await using var db = Context();
            db.ValidationRuns.Add(new ValidationRun { Id = id, DraftId = draftId, Status = status,
                Name = "isolated recovery fixture", DefinitionJson = JsonSerializer.Serialize<RecipeDefinition>(new ClassicalRecipeDefinition(new RecipeClassicalSettings())),
                TargetsJson = JsonSerializer.Serialize(new RecipeTargets(0, 0, 10000)), SnapshotHash = new string('a', 64),
                ManifestHash = inputHash, TruthHash = truthHash, Total = 200, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task<ValidationRun> WaitForFinished(Guid id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var elapsed = Stopwatch.StartNew();
            var sqlTimeouts = 0;
            while (true)
            {
                try
                {
                    await using var db = Context();
                    var run = await db.ValidationRuns.AsNoTracking().SingleAsync(row => row.Id == id, timeout.Token);
                    if (run.Status is "Completed" or "Failed")
                    {
                        output.WriteLine($"WaitForFinished {id}: status={run.Status}, SQL timeouts={sqlTimeouts}, elapsed={elapsed.Elapsed.TotalSeconds:F2}s");
                        return run;
                    }
                }
                // ALTER DATABASE ONLINE can return before LocalDB is ready to
                // complete a fresh login/query. The observer must wait for the
                // same database to become readable; worker state is still checked.
                catch (SqlException error) when (error.Number == -2 && !timeout.IsCancellationRequested)
                {
                    sqlTimeouts++;
                    output.WriteLine($"WaitForFinished {id}: SQL timeout -2 at {DateTimeOffset.UtcNow:O}, elapsed={elapsed.Elapsed.TotalSeconds:F2}s");
                }
                await Task.Delay(100, timeout.Token);
            }
        }

        public async Task SetOnline(bool value)
        {
            await Sql($"ALTER DATABASE [{database}] SET " + (value ? "ONLINE" : "OFFLINE WITH ROLLBACK IMMEDIATE"));
            online = value;
        }

        private async Task CreateImages()
        {
            Directory.CreateDirectory(Path.Combine(directory, "data"));
            Directory.CreateDirectory(Path.Combine(directory, "manifests", "inputs"));
            Directory.CreateDirectory(Path.Combine(directory, "manifests", "truth"));
            using var image = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
            Cv2.Rectangle(image, new Rect(100, 100, 300, 20), Scalar.Black, -1);
            var bytes = image.ImEncode(".png");
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await File.WriteAllBytesAsync(Path.Combine(directory, "data", "reference.png"), bytes);
            var input = Path.Combine(directory, "manifests", "inputs", "validation.jsonl");
            var truth = Path.Combine(directory, "manifests", "truth", "validation.jsonl");
            await File.WriteAllLinesAsync(input, Enumerable.Range(0, 200).Select(i => JsonSerializer.Serialize(new {
                sampleId = i.ToString("D3"), image = "reference.png", imageSha256 = hash, reference = "reference.png", referenceSha256 = hash })));
            await File.WriteAllLinesAsync(truth, Enumerable.Range(0, 200).Select(i => JsonSerializer.Serialize(new { sampleId = i.ToString("D3"), defects = Array.Empty<object>() })));
            inputHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(input)));
            truthHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(truth)));
        }

        private async Task Sql(string commandText)
        {
            if (!database.StartsWith(Prefix, StringComparison.Ordinal) || database.Length != Prefix.Length + 32 ||
                !database.AsSpan(Prefix.Length).ToString().All(Uri.IsHexDigit))
                throw new InvalidOperationException("Unsafe recovery-test database target.");
            await using var connection = new SqlConnection(Master); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = commandText;
            await command.ExecuteNonQueryAsync();
        }

        public async Task Stop()
        {
            Client?.Dispose();
            if (factory is not null) { await factory.DisposeAsync(); factory = null; }
        }

        public async ValueTask DisposeAsync()
        {
            Fault?.Release.TrySetResult();
            await Stop();
            if (!online) await SetOnline(true);
            await Sql($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
            var target = Path.GetFullPath(directory);
            var expectedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetDirectoryName(target) != expectedParent ||
                !Path.GetFileName(target).StartsWith("BoardTrace_WorkerRecovery_", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe recovery-test directory target.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
